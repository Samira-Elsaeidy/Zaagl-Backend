using System.Security.Cryptography;
using System.Text;

namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>
/// A non-secret fingerprint of the scramble key, so every API instance and the coordinator can detect
/// a deployment with the wrong key without ever sharing the key itself (spec §5.4). The coordinator
/// never receives the scramble key; it only ever sees this fingerprint, recorded once at
/// initialisation and compared on every lease.
/// </summary>
public static class KeyFingerprint
{
    private const string Prefix = "zaagl/key-fingerprint/v1";

    /// <summary>Lowercase hex of SHA-256(UTF8(prefix) || key bytes).</summary>
    public static string Compute(byte[] keyBytes)
    {
        var prefixBytes = Encoding.UTF8.GetBytes(Prefix);
        var message = new byte[prefixBytes.Length + keyBytes.Length];
        prefixBytes.CopyTo(message, 0);
        keyBytes.CopyTo(message, prefixBytes.Length);
        var hash = SHA256.HashData(message);
        return Convert.ToHexStringLower(hash);
    }

    public static string Compute(string key) => Compute(Encoding.UTF8.GetBytes(key));
}
