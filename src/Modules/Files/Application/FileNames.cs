using System.Globalization;
using System.Text;

namespace Zaagl.Modules.Files.Application;

/// <summary>
/// The file name sanitiser (ARC-009 section 9.3, addendum 7.2), used for <c>display_name</c> only. A name is never part of a
/// storage path: every key comes from <c>StorageKeys.Create</c>.
/// </summary>
public static class FileNames
{
    private const int MaxBytes = 255;
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <param name="detectedExtension">With the dot; appended when the name's extension is not one of the detected type's.</param>
    public static string Sanitize(string? declared, string? detectedExtension)
    {
        var name = (declared ?? "").Normalize(NormalizationForm.FormC);

        // The last segment after / or \.
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) name = name[(cut + 1)..];

        var sb = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is < 0x20 or (>= 0x7F and <= 0x9F)) continue;                                   // control characters
            if (v is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0x200E or 0x200F) continue; // bidi controls
            if (v is '<' or '>' or ':' or '"' or '|' or '?' or '*') { sb.Append('_'); continue; }
            sb.Append(rune.ToString());
        }
        name = sb.ToString().Trim(' ', '.');

        var firstDot = name.IndexOf('.');
        var stem = firstDot < 0 ? name : name[..firstDot];
        if (ReservedDeviceNames.Contains(stem)) name = "_" + name;

        name = CutToBytes(name, MaxBytes);
        if (name.Length == 0) name = "file";

        if (detectedExtension is { Length: > 0 } ext && !HasExtension(name, ext))
        {
            var room = MaxBytes - Encoding.UTF8.GetByteCount(ext);
            name = CutToBytes(name, room) + ext;
        }
        return name;
    }

    private static bool HasExtension(string name, string detectedExtension)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return false;
        var actual = name[dot..];
        if (actual.Equals(detectedExtension, StringComparison.OrdinalIgnoreCase)) return true;
        // jpg and jpeg are the same type; the others have one spelling.
        return detectedExtension is ".jpg" && actual.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cuts to <paramref name="maxBytes"/> UTF-8 bytes on a character boundary, keeping the extension.</summary>
    private static string CutToBytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var dot = value.LastIndexOf('.');
        var extension = dot > 0 && value.Length - dot <= 16 ? value[dot..] : "";
        var stem = extension.Length > 0 ? value[..dot] : value;
        var budget = maxBytes - Encoding.UTF8.GetByteCount(extension);
        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in stem.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > budget) break;
            sb.Append(rune.ToString());
            used += size;
        }
        return (sb.ToString() + extension).Trim(' ', '.') is { Length: > 0 } cut ? cut : "file";
    }

    /// <summary>Decodes the tus <c>Upload-Metadata</c> header: <c>key base64value,key2 base64value2</c>; a key with no value is
    /// allowed. Returns null when the header is malformed.</summary>
    public static Dictionary<string, string>? ParseTusMetadata(string? header)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(header)) return result;
        foreach (var pair in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = pair.IndexOf(' ');
            var key = space < 0 ? pair : pair[..space];
            if (key.Length == 0 || key.Length > 64 || key.Any(c => c is < '!' or > '~')) return null;
            var value = "";
            if (space >= 0)
            {
                try
                {
                    var bytes = Convert.FromBase64String(PadBase64(pair[(space + 1)..].Trim()));
                    value = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                }
                catch (FormatException) { return null; }
                catch (ArgumentException) { return null; }
            }
            if (!result.TryAdd(key, value)) return null; // a duplicated key is refused (tus 1.0)
        }
        return result;
    }

    private static string PadBase64(string value) =>
        value.Length % 4 == 0 ? value : value.PadRight(value.Length + (4 - value.Length % 4), '=');

    internal static string InvariantInt(long value) => value.ToString(CultureInfo.InvariantCulture);
}
