using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;

namespace EmotePurge.Infrastructure.Services;

// AES-256-GCM with a random 96-bit nonce per encryption; stored value is
// base64(nonce || ciphertext || tag). Deliberately not ASP.NET Core Data Protection: its
// registration is Api-only and conditional (no DataProtection:KeyPath, no provider), the package
// would be an ASP.NET-Core reference inside Infrastructure, and a separate key keeps the DB
// tokens decoupled from the cookie key ring — losing one never invalidates the other.
//
// Key validation is deferred to first use (Lazy), not the constructor: the class is injected into
// UserService, which also serves OnValidatePrincipal on every authenticated request — a missing
// key must break only the token store/load paths (login callback, refresh), matching how a
// missing Auth:Twitch:ClientSecret only breaks the login endpoint.
public sealed class AesGcmTokenCipher(IConfiguration configuration) : ITokenCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly Lazy<byte[]> _key = new(() =>
    {
        var keyBase64 = configuration["Auth:Twitch:TokenEncryptionKey"]
            ?? throw new InvalidOperationException(
                "Konfigurationswert 'Auth:Twitch:TokenEncryptionKey' fehlt (32-Byte-Schlüssel, base64 — z. B. per 'openssl rand -base64 32' erzeugen).");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyBase64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("'Auth:Twitch:TokenEncryptionKey' ist kein gültiges Base64.");
        }

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException(
                $"'Auth:Twitch:TokenEncryptionKey' muss decodiert {KeySize} Byte lang sein, ist aber {key.Length} Byte.");
        }

        return key;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public string Protect(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var packed = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(packed, 0);
        ciphertext.CopyTo(packed, NonceSize);
        tag.CopyTo(packed, NonceSize + ciphertext.Length);
        return Convert.ToBase64String(packed);
    }

    public string? Unprotect(string ciphertext)
    {
        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(ciphertext);
        }
        catch (FormatException)
        {
            return null;
        }

        if (packed.Length < NonceSize + TagSize)
        {
            return null;
        }

        var nonce = packed.AsSpan(0, NonceSize);
        var payload = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize, TagSize);
        var plaintext = new byte[payload.Length];

        try
        {
            using var aes = new AesGcm(_key.Value, TagSize);
            aes.Decrypt(nonce, payload, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
