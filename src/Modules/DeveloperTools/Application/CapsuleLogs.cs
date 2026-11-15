using System.Globalization;
using System.Text;
using System.Text.Json;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.DeveloperTools.Application;

/// <summary>Counts shown on the capsule card without parsing the logs again.</summary>
public sealed record CapsuleLogSummary(int ConsoleCount, int ErrorCount, int NetworkCount, int FailedRequestCount);

/// <summary>
/// The bug-capsule log document, version 1 (docs/phase2/bug-capsules.md "Log schema"). Strict: unknown fields, wrong types, NUL
/// characters, too many entries or too long strings are a <c>400</c>; nothing is coerced silently. The document is rewritten field by
/// field (so only known fields are ever stored) and sensitive values are redacted on the way as defence in depth: header values of
/// <see cref="SensitiveHeaders"/> and query values of <see cref="SensitiveQueryKeys"/> become <c>[redacted]</c>. Bodies are never part
/// of the schema. The caller has already checked the plan's byte cap on the raw document.
/// </summary>
public static class CapsuleLogs
{
    public const int SchemaVersion = 1;
    public const int MaxEntries = 5000;
    public const int MaxHeaders = 50;
    public const string Redacted = "[redacted]";

    public static readonly IReadOnlySet<string> Levels = new HashSet<string>(StringComparer.Ordinal) { "error", "warn", "info", "log", "debug" };

    public static readonly IReadOnlySet<string> SensitiveHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-auth-token", "x-csrf-token", "x-xsrf-token",
        "x-zaagl-link-key", "x-zaagl-grant", "captcha-token",
    };

    public static readonly IReadOnlySet<string> SensitiveQueryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "refresh_token", "id_token", "key", "api_key", "apikey", "secret", "client_secret", "password", "pass",
        "pwd", "auth", "code", "session", "sessionid", "sig", "signature", "credential", "api-key", "x-api-key", "access-token",
        "auth_token", "authtoken", "jwt", "bearer", "private_key", "passwd", "x-amz-signature", "x-amz-credential", "x-amz-security-token",
    };

    /// <summary>Validates <paramref name="logs"/> and returns the normalised, redacted JSON and its summary.</summary>
    public static Result<(string Json, CapsuleLogSummary Summary)> Normalize(JsonElement logs)
    {
        if (logs.ValueKind != JsonValueKind.Object) return Error.Validation("logs must be a JSON object.");
        using var buffer = new MemoryStream();
        var console = 0;
        var errors = 0;
        var network = 0;
        var failed = 0;
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            var sawVersion = false;
            foreach (var p in logs.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "v":
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var v) || v != SchemaVersion)
                            return Error.Validation("logs.v must be 1.");
                        sawVersion = true;
                        break;
                    case "console":
                    {
                        if (p.Value.ValueKind != JsonValueKind.Array) return Error.Validation("logs.console must be an array.");
                        if (p.Value.GetArrayLength() > MaxEntries) return Error.Validation($"logs.console has more than {MaxEntries} entries.");
                        w.WriteStartArray("console");
                        foreach (var e in p.Value.EnumerateArray())
                        {
                            var entry = ConsoleEntry(w, e, out var isError);
                            if (entry is not null) return entry;
                            console++;
                            if (isError) errors++;
                        }
                        w.WriteEndArray();
                        break;
                    }
                    case "network":
                    {
                        if (p.Value.ValueKind != JsonValueKind.Array) return Error.Validation("logs.network must be an array.");
                        if (p.Value.GetArrayLength() > MaxEntries) return Error.Validation($"logs.network has more than {MaxEntries} entries.");
                        w.WriteStartArray("network");
                        foreach (var e in p.Value.EnumerateArray())
                        {
                            var entry = NetworkEntry(w, e, out var isFailure);
                            if (entry is not null) return entry;
                            network++;
                            if (isFailure) failed++;
                        }
                        w.WriteEndArray();
                        break;
                    }
                    case "environment":
                    {
                        var env = Environment(w, p.Value);
                        if (env is not null) return env;
                        break;
                    }
                    default:
                        return Error.Validation($"logs has an unknown field '{Trim(p.Name)}'.");
                }
            }
            if (!sawVersion) return Error.Validation("logs.v is required (1).");
            w.WriteNumber("v", SchemaVersion);
            w.WriteEndObject();
        }
        return (Encoding.UTF8.GetString(buffer.ToArray()), new CapsuleLogSummary(console, errors, network, failed));
    }

    private static Error? ConsoleEntry(Utf8JsonWriter w, JsonElement e, out bool isError)
    {
        isError = false;
        if (e.ValueKind != JsonValueKind.Object) return Error.Validation("Each console entry must be an object.");
        w.WriteStartObject();
        var sawLevel = false;
        foreach (var p in e.EnumerateObject())
        {
            Error? error = p.Name switch
            {
                "t" => Number(w, p, 0, double.MaxValue),
                "level" => Level(w, p, out sawLevel, out isError),
                "message" => Text(w, p, 8192),
                "source" => Text(w, p, 2048),
                "stack" => Text(w, p, 16384),
                _ => Error.Validation($"A console entry has an unknown field '{Trim(p.Name)}'."),
            };
            if (error is not null) return error;
        }
        if (!sawLevel) return Error.Validation("Each console entry needs a level.");
        w.WriteEndObject();
        return null;
    }

    private static Error? Level(Utf8JsonWriter w, JsonProperty p, out bool saw, out bool isError)
    {
        saw = false;
        isError = false;
        if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { } level || !Levels.Contains(level))
            return Error.Validation("A console entry's level must be error, warn, info, log or debug.");
        saw = true;
        isError = level == "error";
        w.WriteString("level", level);
        return null;
    }

    private static Error? NetworkEntry(Utf8JsonWriter w, JsonElement e, out bool isFailure)
    {
        isFailure = false;
        if (e.ValueKind != JsonValueKind.Object) return Error.Validation("Each network entry must be an object.");
        w.WriteStartObject();
        foreach (var p in e.EnumerateObject())
        {
            Error? error;
            switch (p.Name)
            {
                case "t": error = Number(w, p, 0, double.MaxValue); break;
                case "method": error = Text(w, p, 16); break;
                case "url":
                    if (p.Value.ValueKind != JsonValueKind.String) { error = Error.Validation("A network entry's url must be a string."); break; }
                    var url = p.Value.GetString()!;
                    if (url.Length > 4096 || url.Contains('\0')) { error = Error.Validation("A network entry's url is too long or invalid."); break; }
                    w.WriteString("url", RedactUrl(url));
                    error = null;
                    break;
                case "status":
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var status) || status is < 0 or > 999)
                    {
                        error = Error.Validation("A network entry's status must be 0-999.");
                        break;
                    }
                    if (status is 0 or >= 400) isFailure = true;
                    w.WriteNumber("status", status);
                    error = null;
                    break;
                case "durationMs": case "requestBodySize": case "responseBodySize": error = Number(w, p, 0, double.MaxValue); break;
                case "type": error = Text(w, p, 32); break;
                case "error":
                    isFailure = true;
                    error = Text(w, p, 2048);
                    break;
                case "requestHeaders": case "responseHeaders": error = Headers(w, p); break;
                default: error = Error.Validation($"A network entry has an unknown field '{Trim(p.Name)}'."); break;
            }
            if (error is not null) return error;
        }
        w.WriteEndObject();
        return null;
    }

    private static Error? Environment(Utf8JsonWriter w, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return Error.Validation("logs.environment must be an object.");
        w.WriteStartObject("environment");
        foreach (var p in e.EnumerateObject())
        {
            Error? error;
            switch (p.Name)
            {
                case "userAgent": error = Text(w, p, 512); break;
                case "platform": error = Text(w, p, 64); break;
                case "language": error = Text(w, p, 35); break;
                case "timestamp": error = Text(w, p, 64); break;
                case "url":
                    if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > 4096 || p.Value.GetString()!.Contains('\0'))
                    {
                        error = Error.Validation("logs.environment.url must be a string of at most 4096 characters.");
                        break;
                    }
                    w.WriteString("url", RedactUrl(p.Value.GetString()!));
                    error = null;
                    break;
                case "viewport":
                    if (p.Value.ValueKind != JsonValueKind.Object) { error = Error.Validation("logs.environment.viewport must be an object."); break; }
                    w.WriteStartObject("viewport");
                    error = null;
                    foreach (var d in p.Value.EnumerateObject())
                    {
                        error = d.Name is "w" or "h" ? Number(w, d, 0, 100_000) : Error.Validation("viewport has only w and h.");
                        if (error is not null) break;
                    }
                    if (error is null) w.WriteEndObject();
                    break;
                default: error = Error.Validation($"logs.environment has an unknown field '{Trim(p.Name)}'."); break;
            }
            if (error is not null) return error;
        }
        w.WriteEndObject();
        return null;
    }

    private static Error? Headers(Utf8JsonWriter w, JsonProperty p)
    {
        if (p.Value.ValueKind != JsonValueKind.Object) return Error.Validation($"{p.Name} must be an object of strings.");
        var count = 0;
        w.WriteStartObject(p.Name);
        foreach (var h in p.Value.EnumerateObject())
        {
            if (++count > MaxHeaders) return Error.Validation($"{p.Name} has more than {MaxHeaders} entries.");
            if (h.Name.Length is 0 or > 256 || h.Name.Contains('\0')) return Error.Validation("A header name is empty, too long or invalid.");
            if (h.Value.ValueKind != JsonValueKind.String) return Error.Validation("Header values must be strings.");
            var value = h.Value.GetString()!;
            if (value.Length > 4096 || value.Contains('\0')) return Error.Validation("A header value is too long or invalid.");
            w.WriteString(h.Name, SensitiveHeaders.Contains(h.Name) ? Redacted : value);
        }
        w.WriteEndObject();
        return null;
    }

    private static Error? Text(Utf8JsonWriter w, JsonProperty p, int max)
    {
        if (p.Value.ValueKind != JsonValueKind.String) return Error.Validation($"'{Trim(p.Name)}' must be a string.");
        var value = p.Value.GetString()!;
        if (value.Contains('\0')) return Error.Validation("Text cannot contain NUL (\\u0000) characters.");
        if (value.Length > max) return Error.Validation($"'{Trim(p.Name)}' is longer than {max} characters.");
        w.WriteString(p.Name, value);
        return null;
    }

    private static Error? Number(Utf8JsonWriter w, JsonProperty p, double min, double max)
    {
        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var n) || !double.IsFinite(n) || n < min || n > max)
            return Error.Validation(double.IsPositiveInfinity(max) || max == double.MaxValue
                ? $"'{Trim(p.Name)}' must be a finite number, {min.ToString(CultureInfo.InvariantCulture)} or more."
                : $"'{Trim(p.Name)}' must be a number between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        w.WriteNumber(p.Name, n);
        return null;
    }

    /// <summary>
    /// Replaces the values of sensitive query parameters (and the fragment, and any user:password before the host) by
    /// <see cref="Redacted"/>; everything else is kept byte for byte. Parameters are split on <c>&amp;</c> and <c>;</c>; a key is
    /// percent-decoded (repeatedly, so double encoding does not hide it, and <c>+</c> as a space), trimmed, and matched
    /// case-insensitively by its base name and by every bracket segment, so <c>token[]</c>, <c>api_key[0]</c>, <c>auth[token]</c>,
    /// <c>user[password]</c> and <c>%74oken</c> are all caught.
    /// </summary>
    public static string RedactUrl(string url)
    {
        url = RedactUserInfo(url);
        var hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash] + (hash < url.Length - 1 ? "#" + Redacted : "");
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var head = url[..(q + 1)];
        var tail = url[(q + 1)..];
        var fragment = "";
        var h = tail.IndexOf('#');
        if (h >= 0) { fragment = tail[h..]; tail = tail[..h]; }
        var output = new StringBuilder(url.Length);
        var start = 0;
        for (var i = 0; i <= tail.Length; i++)
        {
            if (i < tail.Length && tail[i] is not ('&' or ';')) continue;
            var part = tail[start..i];
            var eq = part.IndexOf('=');
            output.Append(eq >= 0 && IsSensitiveKey(part[..eq]) ? part[..(eq + 1)] + Redacted : part);
            if (i < tail.Length) output.Append(tail[i]);
            start = i + 1;
        }
        return head + output + fragment;
    }

    /// <summary>True when the raw query key names a sensitive parameter (see <see cref="RedactUrl"/>).</summary>
    public static bool IsSensitiveKey(string rawKey)
    {
        var key = rawKey;
        for (var round = 0; round < 3; round++)
        {
            var decoded = Uri.UnescapeDataString(key.Replace('+', ' '));
            if (decoded == key) break;
            key = decoded;
        }
        key = key.Trim();
        // "a[b][0]" -> segments "a", "b", "0"; "token[]" -> "token".
        foreach (var segment in key.Split('[', ']'))
        {
            var name = segment.Trim();
            if (name.Length > 0 && SensitiveQueryKeys.Contains(name)) return true;
        }
        return false;
    }

    /// <summary>scheme://user:password@host... -> scheme://[redacted]@host...</summary>
    private static string RedactUserInfo(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;
        var authorityStart = scheme + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? url[authorityStart..] : url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        if (at < 0) return url;
        return url[..authorityStart] + Redacted + url[(authorityStart + at)..];
    }

    private static string Trim(string name) => name.Length <= 40 ? name : name[..40];
}
