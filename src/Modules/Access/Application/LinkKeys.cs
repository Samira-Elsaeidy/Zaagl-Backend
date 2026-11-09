using System.Buffers.Text;
using System.Security.Cryptography;
using Zaagl.Modules.Access.Contracts;

namespace Zaagl.Modules.Access.Application;

/// <summary>
/// C4 <see cref="ILinkKeys"/> (ARC-004 §2, contracts.md C4 "Link key format"): 16 CSPRNG bytes, base64url without
/// padding (22 characters); the stored hash is SHA-256 of the 16 raw bytes. Anything that is not exactly 22 base64url
/// characters decoding to 16 bytes is a wrong key. Comparison is constant-time. The Links issuer mints keys itself
/// (contracts.md change F-2); this class is the verifier.
/// </summary>
public sealed class LinkKeys : ILinkKeys
{
    /// <summary>Compared against for unknown codes, so a miss costs one hash compare like a wrong key (ARC-004 §2).</summary>
    private static readonly byte[] DummyHash = SHA256.HashData(new byte[AccessWireNames.LinkKeyBytes]);

    public NewLinkKey Generate()
    {
        var raw = RandomNumberGenerator.GetBytes(AccessWireNames.LinkKeyBytes);
        return new NewLinkKey(Base64Url.EncodeToString(raw), SHA256.HashData(raw));
    }

    public bool Matches(string? presentedKey, byte[] storedSha256Hash)
    {
        Span<byte> raw = stackalloc byte[AccessWireNames.LinkKeyBytes];
        var decoded = TryDecode(presentedKey, raw);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(decoded ? raw : new byte[AccessWireNames.LinkKeyBytes], hash);
        var equal = storedSha256Hash.Length == 32 && CryptographicOperations.FixedTimeEquals(hash, storedSha256Hash);
        return decoded && equal;
    }

    /// <summary>The dummy compare for a code that does not exist (same timing class as a wrong key).</summary>
    public void DummyCompare(string? presentedKey) => Matches(presentedKey, DummyHash);

    private static bool TryDecode(string? key, Span<byte> raw)
    {
        if (key is null || key.Length != AccessWireNames.LinkKeyEncodedLength) return false;
        foreach (var ch in key)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return false;
        return Base64Url.TryDecodeFromChars(key, raw, out var written) && written == AccessWireNames.LinkKeyBytes;
    }
}
