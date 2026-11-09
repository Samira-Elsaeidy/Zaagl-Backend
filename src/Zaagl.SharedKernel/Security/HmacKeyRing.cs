using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Zaagl.SharedKernel.Security;

/// <summary>One <c>{ kid, key }</c> entry of a rotating HMAC key set (configuration shape shared by
/// <c>Access:GrantKeys</c> and <c>Content:TicketKeys</c>). The key is base64 (32 bytes or more).</summary>
public sealed class HmacKeyEntry
{
    public string Kid { get; set; } = "";
    public string Key { get; set; } = "";
}

/// <summary>
/// The token shape shared by access grants (ARC-004 §4.2) and delivery tickets (ARC-008 §3):
/// <c>b64url(payload) "." b64url(HMAC-SHA256(key[kid], domain || payload))</c>. The first entry signs, every entry
/// verifies (rotation: add a new first entry, deploy, remove the old one after the token lifetime). Different
/// domain strings and different key rings keep the two token kinds apart. Pure: no I/O, no clock.
/// </summary>
public sealed class HmacKeyRing
{
    /// <summary>ARC-004 §4.3 / ARC-008 §3: any token longer than this is refused before decoding.</summary>
    public const int MaxTokenLength = 256;

    public const int MinKeyBytes = 32;
    public const int MaxKidLength = 16;

    private readonly byte[] _domain;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public string SigningKid { get; }

    public HmacKeyRing(string domain, IEnumerable<HmacKeyEntry> entries)
    {
        _domain = Encoding.ASCII.GetBytes(domain);
        string? first = null;
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Kid) || entry.Kid.Length > MaxKidLength || !entry.Kid.All(char.IsAscii))
                throw new InvalidOperationException($"Every key of '{domain.Trim()}' needs an ASCII kid of 1-{MaxKidLength} characters.");
            byte[] key;
            try { key = Convert.FromBase64String(entry.Key); }
            catch (FormatException) { throw new InvalidOperationException($"Key '{entry.Kid}' of '{domain.Trim()}' is not base64."); }
            if (key.Length < MinKeyBytes)
                throw new InvalidOperationException($"Key '{entry.Kid}' of '{domain.Trim()}' must be at least {MinKeyBytes} bytes.");
            if (!_keys.TryAdd(entry.Kid, key))
                throw new InvalidOperationException($"Duplicate kid '{entry.Kid}' in '{domain.Trim()}'.");
            first ??= entry.Kid;
        }
        SigningKid = first ?? throw new InvalidOperationException($"The key ring '{domain.Trim()}' is empty.");
    }

    /// <summary>A ring with one random key, for development and tests when nothing is configured.</summary>
    public static HmacKeyRing Ephemeral(string domain) =>
        new(domain, [new HmacKeyEntry { Kid = "ephemeral", Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }]);

    public bool HasKid(string kid) => _keys.ContainsKey(kid);

    public string Sign(ReadOnlySpan<byte> payload, string kid)
    {
        var mac = Mac(_keys[kid], payload);
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);
    }

    /// <summary>Decodes the two parts and returns the payload, or null for a malformed token. The MAC is NOT
    /// checked here (the kid lives inside the payload); call <see cref="VerifyMac"/> with the payload's kid.</summary>
    public static (byte[] Payload, byte[] Mac)? Split(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot != token.LastIndexOf('.') || dot == token.Length - 1) return null;
        try
        {
            var payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            var mac = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
            return mac.Length == 32 ? (payload, mac) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Constant-time comparison of the presented MAC with the one computed under <paramref name="kid"/>.</summary>
    public bool VerifyMac(string kid, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> mac) =>
        _keys.TryGetValue(kid, out var key) && CryptographicOperations.FixedTimeEquals(Mac(key, payload), mac);

    private byte[] Mac(byte[] key, ReadOnlySpan<byte> payload)
    {
        var material = new byte[_domain.Length + payload.Length];
        _domain.CopyTo(material, 0);
        payload.CopyTo(material.AsSpan(_domain.Length));
        return HMACSHA256.HashData(key, material);
    }
}
