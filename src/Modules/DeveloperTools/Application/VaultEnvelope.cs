using System.Buffers.Text;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.DeveloperTools.Application;

/// <summary>
/// Structural check of a client-side vault envelope (docs/phase2/vault.md, "Client-side crypto contract"). The server never
/// decrypts and never looks past the header: it checks base64url, size, version, flags and minimum length only.
/// Version 1: <c>0x01 | flags | [salt 16 when flags bit 0] | iv 12 | AES-256-GCM ciphertext + 16-byte tag</c>.
/// </summary>
public static class VaultEnvelope
{
    public const byte Version1 = 0x01;
    public const byte FlagPassphrase = 0x01;
    public const int SaltBytes = 16;
    public const int IvBytes = 12;
    public const int TagBytes = 16;

    /// <summary>Envelope versions this server accepts.</summary>
    public static readonly IReadOnlySet<byte> KnownVersions = new HashSet<byte> { Version1 };

    /// <summary>Smallest version-1 envelope without a salt: version, flags, IV and tag (an empty plaintext). The server assumes
    /// nothing about the plaintext. Matches the <c>ck_secrets_ciphertext_bytes</c> lower bound (30).</summary>
    public const int MinBytes = 2 + IvBytes + TagBytes;

    /// <summary>Decodes and checks the envelope. <paramref name="maxBytes"/> is the plan cap of the decoded envelope.</summary>
    public static Result<byte[]> Parse(string? encoded, bool passphraseProtected, int maxBytes, out bool tooLarge)
    {
        tooLarge = false;
        if (string.IsNullOrEmpty(encoded)) return Error.Validation("ciphertext is required.");
        // Cheap length bound before decoding: base64url carries 3 bytes per 4 characters.
        if ((long)encoded.Length > (long)maxBytes * 4 / 3 + 4)
        {
            tooLarge = true;
            return Error.Validation("The ciphertext is too large for your plan.");
        }
        foreach (var c in encoded)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return Error.Validation("ciphertext must be base64url without padding.");
        }
        if (!Base64Url.IsValid(encoded, out var decodedLength))
            return Error.Validation("ciphertext must be base64url without padding.");
        if (decodedLength > maxBytes)
        {
            tooLarge = true;
            return Error.Validation("The ciphertext is too large for your plan.");
        }
        var bytes = Base64Url.DecodeFromChars(encoded);
        if (bytes.Length < MinBytes) return Error.Validation("The ciphertext envelope is too short.");
        if (!KnownVersions.Contains(bytes[0])) return Error.Validation("Unknown ciphertext envelope version.");
        var flags = bytes[1];
        if ((flags & ~FlagPassphrase) != 0) return Error.Validation("Unknown ciphertext envelope flags.");
        var hasSalt = (flags & FlagPassphrase) != 0;
        if (hasSalt != passphraseProtected)
            return Error.Validation("passphraseProtected does not match the envelope (flags bit 0).");
        if (bytes.Length < MinBytes + (hasSalt ? SaltBytes : 0)) return Error.Validation("The ciphertext envelope is too short.");
        return bytes;
    }
}
