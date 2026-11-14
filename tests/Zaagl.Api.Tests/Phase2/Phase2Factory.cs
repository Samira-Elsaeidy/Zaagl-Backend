using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zaagl.Api.Tests.Harness;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 (docs/phase2): the API with a user-content host, ticket keys and no write/read throttling, so tests exercise
/// the product rules (plan caps, view limits, races) rather than the per-minute limiters (those are tested on their own).</summary>
public class Phase2Factory : ZaaglApiFactory
{
    public const string UserContentHost = "usercontent.zaa.test";

    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
    [
        new("Hosts:UserContent", UserContentHost),
        new("Content:UserContentBaseUrl", $"http://{UserContentHost}"),
        new("Content:TicketKeys:0:Kid", "p2"),
        new("Content:TicketKeys:0:Key", Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("phase2-tests-ticket-key")))),
        new("Hosts:RewriteDeliveryPath", "true"),
        new("RateLimits:ContentWritePerMinute", "100000"),
        new("RateLimits:ContentReadPerMinute", "100000"),
        new("RateLimits:UnlockPerMinute", "100000"),
        new("Guests:PerMinute", "100000"),
    ];
}

/// <summary>Small HTTP helpers shared by the Phase 2 tests.</summary>
public static class P2
{
    public static readonly JsonSerializerOptions Json = TestPrincipals.Json;

    public static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null,
        IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(k, v);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static async Task<HttpResponseMessage> RawAsync(HttpClient client, HttpMethod method, string url, object? body = null,
        IDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(k, v);
        return await client.SendAsync(request);
    }

    public static Dictionary<string, string> Key(string? linkKey, string? grant = null)
    {
        var headers = new Dictionary<string, string>();
        if (linkKey is not null) headers["X-Zaagl-Link-Key"] = linkKey;
        if (grant is not null) headers["X-Zaagl-Grant"] = grant;
        return headers;
    }

    public static string Code(this JsonElement e) => e.GetProperty("code").GetString()!;
    public static Guid ResourceId(this JsonElement e) => e.GetProperty("resourceId").GetGuid();
    public static string ProblemCode(this JsonElement e) => e.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
}

/// <summary>
/// An independent implementation of the vault's client-side crypto contract (docs/phase2/vault.md, version 1), written from the
/// document only, so the tests prove the contract is implementable and that the server stores exactly the envelope it receives.
/// </summary>
public static class VaultCrypto
{
    public static (string Envelope, string EncKey) Encrypt(string plaintextJson, string? passphrase = null)
    {
        var encKey = RandomNumberGenerator.GetBytes(32);
        var salt = passphrase is null ? null : RandomNumberGenerator.GetBytes(16);
        var header = new List<byte> { 0x01, (byte)(passphrase is null ? 0 : 1) };
        if (salt is not null) header.AddRange(salt);
        var cek = Cek(encKey, salt, passphrase);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintextJson);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(iv, plain, cipher, tag, header.ToArray());
        var envelope = header.Concat(iv).Concat(cipher).Concat(tag).ToArray();
        return (Base64Url(envelope), Base64Url(encKey));
    }

    public static string Decrypt(string envelopeB64, string encKeyB64, string? passphrase = null)
    {
        var envelope = FromBase64Url(envelopeB64);
        var hasSalt = (envelope[1] & 1) != 0;
        var headerLength = 2 + (hasSalt ? 16 : 0);
        var header = envelope[..headerLength];
        var salt = hasSalt ? envelope[2..18] : null;
        var iv = envelope[headerLength..(headerLength + 12)];
        var body = envelope[(headerLength + 12)..];
        var cipher = body[..^16];
        var tag = body[^16..];
        var cek = Cek(FromBase64Url(encKeyB64), salt, passphrase);
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(cek, 16)) gcm.Decrypt(iv, cipher, tag, plain, header);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Cek(byte[] encKey, byte[]? salt, string? passphrase)
    {
        var ikm = encKey;
        if (passphrase is not null)
        {
            var pk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt!, 600_000, HashAlgorithmName.SHA256, 32);
            ikm = encKey.Concat(pk).ToArray();
        }
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt ?? new byte[16], Encoding.UTF8.GetBytes("zaagl/vault/v1/cek"));
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
