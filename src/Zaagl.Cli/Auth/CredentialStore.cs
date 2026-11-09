using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Auth;

/// <summary>
/// What the CLI keeps in the OS credential store for one API host (CLI-004). Short property names keep the blob small
/// (Windows Credential Manager allows 2,560 bytes). The access token is cached here too so consecutive commands do not
/// rotate the refresh token on every run; it is dropped when the blob would not fit. Nothing here is ever written to
/// a plain file.
/// </summary>
public sealed record StoredCredential(
    [property: JsonPropertyName("rt")] string RefreshToken,
    [property: JsonPropertyName("rtx")] DateTimeOffset? RefreshTokenExpiresAt,
    [property: JsonPropertyName("at")] string? AccessToken,
    [property: JsonPropertyName("ati")] DateTimeOffset? AccessTokenIssuedAt,
    [property: JsonPropertyName("atx")] DateTimeOffset? AccessTokenExpiresAt,
    [property: JsonPropertyName("sid")] Guid? SessionId,
    [property: JsonPropertyName("uid")] Guid? UserId,
    [property: JsonPropertyName("em")] string? Email,
    [property: JsonPropertyName("dn")] string? DisplayName)
{
    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE (5 * 512 bytes).</summary>
    public const int MaxBlobBytes = 2560;

    /// <summary>Proactive refresh at 80 % of the access token's life (DESKTOP_CLI_PLAN.md §3), and never within 30 s
    /// of expiry.</summary>
    public bool HasFreshAccessToken(DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(AccessToken) || AccessTokenExpiresAt is not { } exp) return false;
        if (exp - now < TimeSpan.FromSeconds(30)) return false;
        if (AccessTokenIssuedAt is { } issued && exp > issued)
            return now < issued + TimeSpan.FromTicks((long)((exp - issued).Ticks * 0.8));
        return true;
    }

    public byte[] Serialize()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, CliJson.Context.StoredCredential);
        if (bytes.Length <= MaxBlobBytes) return bytes;
        // Too large for the smallest store: keep only what is needed to refresh.
        bytes = JsonSerializer.SerializeToUtf8Bytes(
            this with { AccessToken = null, AccessTokenExpiresAt = null, AccessTokenIssuedAt = null, DisplayName = null },
            CliJson.Context.StoredCredential);
        if (bytes.Length > MaxBlobBytes)
            throw new CliException(ExitCodes.Unexpected, "The sign-in data is too large for the credential store.", "credential-too-large");
        return bytes;
    }

    public static StoredCredential? Deserialize(byte[] blob)
    {
        try
        {
            var c = JsonSerializer.Deserialize(blob, CliJson.Context.StoredCredential);
            return c is null || string.IsNullOrEmpty(c.RefreshToken) ? null : c;
        }
        catch (JsonException)
        {
            return null; // corrupt entry: behave as signed out
        }
    }
}

/// <summary>An OS secret store. Keys are opaque names; values are small blobs.</summary>
public interface ICredentialStore
{
    /// <summary>Human-readable name for messages ("Windows Credential Manager").</summary>
    string Name { get; }

    byte[]? Read(string key);

    /// <exception cref="CredentialStoreUnavailableException">No usable store on this machine.</exception>
    void Write(string key, byte[] secret);

    /// <summary>True when an entry existed.</summary>
    bool Delete(string key);
}

public sealed class CredentialStoreUnavailableException(string message) : CliException(ExitCodes.Auth, message, "credential-store-unavailable");

public static class CredentialStores
{
    /// <summary>Environment variables that bypass the store (precedence: API key, then token, then the store).</summary>
    public const string ApiKeyVariable = "ZAAGL_API_KEY";
    public const string TokenVariable = "ZAAGL_TOKEN";

    public static string HeadlessGuidance =>
        "No OS credential store is available on this machine, and zaagl never stores tokens in plain files. " +
        $"Set {TokenVariable} (a short-lived access token) or {ApiKeyVariable} (paid plans) instead.";

    public static ICredentialStore CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainStore();
        if (OperatingSystem.IsLinux()) return new LinuxSecretServiceStore();
        return new UnavailableCredentialStore();
    }

    /// <summary>One entry per API host so development, staging and production sessions never mix.</summary>
    public static string KeyFor(Uri apiBase) =>
        "zaa.gl/cli/" + apiBase.Host.ToLowerInvariant() + (apiBase.IsDefaultPort ? "" : ":" + apiBase.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Lock-file name for a key (no secret, no host name characters that are invalid in file names).</summary>
    public static string LockNameFor(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "auth-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

public sealed class UnavailableCredentialStore : ICredentialStore
{
    public string Name => "none";
    public byte[]? Read(string key) => null;
    public void Write(string key, byte[] secret) => throw new CredentialStoreUnavailableException(CredentialStores.HeadlessGuidance);
    public bool Delete(string key) => false;
}
