namespace EmotePurge.Core.Services;

// Encrypts long-lived Twitch tokens before they are persisted in Postgres, so a database dump
// (or a backup of one) alone never exposes usable credentials — decryption additionally requires
// the key from the environment (.env / user-secrets), which lives outside every DB backup.
public interface ITokenCipher
{
    string Protect(string plaintext);

    // Returns null when the ciphertext cannot be decrypted (tampered with, or written under a
    // different key) — callers treat that exactly like "no token stored".
    string? Unprotect(string ciphertext);
}
