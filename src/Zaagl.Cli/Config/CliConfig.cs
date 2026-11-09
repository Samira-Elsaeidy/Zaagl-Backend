using System.Text.Json;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Config;

/// <summary>Where the non-secret settings file and the lock files live (DESKTOP_CLI_PLAN.md §4, <c>config</c>).</summary>
public static class ConfigPaths
{
    public const string OverrideVariable = "ZAAGL_CONFIG_DIR";

    public static string GetConfigDirectory(Func<string, string?> env)
    {
        var overridden = env(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zaagl");

        var home = env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "zaagl");

        var xdg = env("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg, "zaagl");
    }

    /// <summary>Creates the directory; on Unix only the owner may read or list it (0700).</summary>
    public static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>The settings keys. The file holds non-secret values only; tokens and keys live in the OS store.</summary>
public static class ConfigKeys
{
    public const string ApiUrl = "api-url";
    public const string DefaultExpiry = "default-expiry";
    public const string Output = "output";
    public const string Color = "color";
    public const string UpdateCheck = "update-check";

    public const string DefaultApiUrl = "https://api.zaa.gl";

    public static readonly IReadOnlyList<string> All = [ApiUrl, DefaultExpiry, Output, Color, UpdateCheck];

    /// <summary>Effective value when a key is not set; null means "server default".</summary>
    public static string? DefaultFor(string key) => key switch
    {
        ApiUrl => DefaultApiUrl,
        Output => "human",
        Color => "auto",
        UpdateCheck => "true",
        _ => null,
    };

    public static bool IsKnown(string key) => All.Contains(key, StringComparer.Ordinal);

    /// <summary>Null when valid, else the reason. Unknown keys and secret-looking values are refused (CLI-010).</summary>
    public static string? Validate(string key, string value)
    {
        if (!IsKnown(key)) return $"Unknown key '{key}'. Known keys: {string.Join(", ", All)}.";
        if (LooksSecret(value)) return "That value looks like a token or key. Secrets are never stored in the config file.";

        return key switch
        {
            ApiUrl => ApiUrlRules.Validate(value),
            DefaultExpiry => ExpiryParser.TryParseDuration(value, out _) ? null : "Use a duration like 30m, 12h, 7d or 2w.",
            Output => value is "human" or "json" or "quiet" ? null : "Use human, json or quiet.",
            Color => value is "auto" or "always" or "never" ? null : "Use auto, always or never.",
            UpdateCheck => value is "true" or "false" ? null : "Use true or false.",
            _ => null,
        };
    }

    /// <summary>API keys (<c>zgl_</c>), JWTs (three base64url segments starting <c>eyJ</c>), bearer strings and PEM
    /// blocks. Belt and braces: every key also has a strict format.</summary>
    public static bool LooksSecret(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("zgl_", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Contains("-----BEGIN", StringComparison.Ordinal)) return true;
        if (v.StartsWith("eyJ", StringComparison.Ordinal) && v.Split('.').Length == 3) return true;
        return false;
    }
}

public static class ApiUrlRules
{
    /// <summary>https only, except plain http to a loopback host for local development. No credentials, query or
    /// fragment in the URL (they would end up in logs and the config file).</summary>
    public static string? Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "Use an absolute URL such as https://api.zaa.gl.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The URL must not contain a user name or password.";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "The URL must not contain a query or fragment.";
        if (uri.Scheme == Uri.UriSchemeHttps) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return null;
        return "Use https (plain http is allowed only for localhost).";
    }

    /// <summary>The base address with a trailing slash so relative paths like <c>api/links</c> resolve under it.</summary>
    public static Uri Normalize(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        var s = uri.GetLeftPart(UriPartial.Path);
        return new Uri(s.EndsWith('/') ? s : s + "/", UriKind.Absolute);
    }
}

/// <summary>The settings file: a flat JSON object of string values.</summary>
public sealed class CliConfig
{
    public const string FileName = "config.json";

    private readonly string _path;

    private CliConfig(string path, Dictionary<string, string> values)
    {
        _path = path;
        Values = values;
    }

    public Dictionary<string, string> Values { get; }

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public static CliConfig Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new CliConfig(path, new(StringComparer.Ordinal));
        try
        {
            var json = File.ReadAllText(path);
            var values = JsonSerializer.Deserialize(json, CliJson.Context.DictionaryStringString) ?? [];
            // Unknown or invalid entries (hand edits, newer versions) are ignored, not trusted.
            var clean = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in values)
                if (ConfigKeys.Validate(k, v) is null) clean[k] = v;
            return new CliConfig(path, clean);
        }
        catch (JsonException)
        {
            throw new CliException(ExitCodes.Unexpected, $"The settings file {path} is not valid JSON. Fix or delete it.", "config-invalid");
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path)!;
        ConfigPaths.EnsureDirectory(dir);
        var sorted = new SortedDictionary<string, string>(Values, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new Dictionary<string, string>(sorted), CliJson.Indented.DictionaryStringString);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json + "\n");
        File.Move(temp, _path, overwrite: true);
    }
}
