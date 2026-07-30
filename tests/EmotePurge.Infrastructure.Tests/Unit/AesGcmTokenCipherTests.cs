using System.Security.Cryptography;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class AesGcmTokenCipherTests
{
    private static AesGcmTokenCipher CreateCipher(string? keyBase64)
    {
        var values = new Dictionary<string, string?>();
        if (keyBase64 is not null)
        {
            values["Auth:Twitch:TokenEncryptionKey"] = keyBase64;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AesGcmTokenCipher(configuration);
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var cipher = CreateCipher(NewKey());

        // Twitch's doc example refresh token contains '%' and '=' — worth round-tripping verbatim.
        const string plaintext = "eyJfMzUtNDU0OC4MWYwLTQ5MDY5ODY4NGNlMSJ9%asdfasdf=";
        var protectedValue = cipher.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedValue);
        Assert.Equal(plaintext, cipher.Unprotect(protectedValue));
    }

    [Fact]
    public void Protect_SamePlaintextTwice_ProducesDifferentCiphertexts()
    {
        // A fresh random nonce per call — identical tokens must not be linkable in the database.
        var cipher = CreateCipher(NewKey());

        Assert.NotEqual(cipher.Protect("token"), cipher.Protect("token"));
    }

    [Fact]
    public void Unprotect_WithDifferentKey_ReturnsNull()
    {
        var protectedValue = CreateCipher(NewKey()).Protect("token");

        Assert.Null(CreateCipher(NewKey()).Unprotect(protectedValue));
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("dG9vLXNob3J0")]
    public void Unprotect_WithGarbage_ReturnsNull(string garbage)
    {
        Assert.Null(CreateCipher(NewKey()).Unprotect(garbage));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_ReturnsNull()
    {
        var cipher = CreateCipher(NewKey());
        var packed = Convert.FromBase64String(cipher.Protect("token"));
        packed[^1] ^= 0xFF;

        Assert.Null(cipher.Unprotect(Convert.ToBase64String(packed)));
    }

    [Fact]
    public void MissingKey_ThrowsOnFirstUse_NotOnConstruction()
    {
        // The cipher is injected into UserService, which also runs on every authenticated request
        // (OnValidatePrincipal) — a missing key must only break the token paths that actually use it.
        var cipher = CreateCipher(null);

        Assert.Throws<InvalidOperationException>(() => cipher.Protect("token"));
    }

    [Theory]
    [InlineData("kein-base64")]
    [InlineData("dG9vLXNob3J0")]
    public void InvalidKey_ThrowsOnFirstUse(string keyBase64)
    {
        var cipher = CreateCipher(keyBase64);

        Assert.Throws<InvalidOperationException>(() => cipher.Protect("token"));
    }
}
