using System.Globalization;
using System.Text;

namespace Zaagl.Cli.Infrastructure;

/// <summary>Removes ANSI/VT escape sequences (colours, cursor movement, OSC titles and hyperlinks) from terminal output.
/// Hand-written state machine: no regex, linear time, safe on truncated input.</summary>
public static class AnsiStripper
{
    private const char Esc = '\u001b';
    private const char Bel = '\u0007';
    private const char Csi8 = '\u009b'; // 8-bit CSI
    private const char Osc8 = '\u009d'; // 8-bit OSC

    public static string Strip(string input)
    {
        if (input.IndexOf(Esc) < 0 && input.IndexOf(Csi8) < 0 && input.IndexOf(Osc8) < 0) return input;

        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (c == Csi8) { i = SkipCsi(input, i + 1); continue; }
            if (c == Osc8) { i = SkipString(input, i + 1); continue; }
            if (c != Esc) { sb.Append(c); i++; continue; }

            if (i + 1 >= input.Length) { i++; continue; } // lone ESC at the end
            var next = input[i + 1];
            switch (next)
            {
                case '[':
                    i = SkipCsi(input, i + 2);
                    break;
                case ']': // OSC ... BEL or ST
                case 'P': // DCS
                case 'X': // SOS
                case '^': // PM
                case '_': // APC
                    i = SkipString(input, i + 2);
                    break;
                default:
                    // Two-character sequence, possibly with intermediate bytes (0x20-0x2F), e.g. ESC ( B.
                    var j = i + 1;
                    while (j < input.Length && input[j] >= ' ' && input[j] <= '/') j++;
                    i = j < input.Length ? j + 1 : j;
                    break;
            }
        }
        return sb.ToString();
    }

    // CSI: parameter bytes 0x30-0x3F, intermediates 0x20-0x2F, final byte 0x40-0x7E.
    private static int SkipCsi(string s, int i)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c >= '@' && c <= '~') return i + 1;
            if (c < ' ' || c > '?') return i; // malformed: drop the partial sequence, keep this character
            i++;
        }
        return i;
    }

    // String sequences end with BEL or ST (ESC \ or 0x9C).
    private static int SkipString(string s, int i)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == Bel || c == '\u009c') return i + 1;
            if (c == Esc && i + 1 < s.Length && s[i + 1] == '\\') return i + 2;
            i++;
        }
        return i;
    }
}

/// <summary>Decodes piped bytes. PowerShell 5.1 writes UTF-16LE files (with BOM) and Windows tools may pipe UTF-16
/// without one; everything else is UTF-8. CLI-008.</summary>
public static class InputDecoder
{
    public sealed record Decoded(string Text, string EncodingName, bool HadInvalidBytes);

    public static Decoded Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return Utf8(bytes[3..], "utf-8-bom");
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
            return new(new UTF32Encoding(false, false).GetString(bytes[4..]), "utf-32le", false);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return new(Encoding.Unicode.GetString(bytes[2..]), "utf-16le", false);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new(Encoding.BigEndianUnicode.GetString(bytes[2..]), "utf-16be", false);

        switch (GuessUtf16(bytes))
        {
            case 1: return new(Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]), "utf-16le", false);
            case 2: return new(Encoding.BigEndianUnicode.GetString(bytes[..(bytes.Length & ~1)]), "utf-16be", false);
        }
        return Utf8(bytes, "utf-8");
    }

    private static Decoded Utf8(ReadOnlySpan<byte> bytes, string name)
    {
        try
        {
            return new(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), name, false);
        }
        catch (DecoderFallbackException)
        {
            return new(new UTF8Encoding(false, false).GetString(bytes), name, true);
        }
    }

    /// <summary>0 = not UTF-16, 1 = little endian, 2 = big endian. Mostly-ASCII UTF-16 text has a zero in every other
    /// byte; UTF-8 text never contains zero bytes unless it contains NUL characters.</summary>
    internal static int GuessUtf16(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes[..(Math.Min(bytes.Length, 4096) & ~1)];
        var pairs = sample.Length / 2;
        if (pairs < 2) return 0;

        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < sample.Length; i += 2)
        {
            if (sample[i] == 0) evenZeros++;
            if (sample[i + 1] == 0) oddZeros++;
        }
        if (oddZeros >= pairs * 0.4 && evenZeros <= pairs * 0.05) return 1;
        if (evenZeros >= pairs * 0.4 && oddZeros <= pairs * 0.05) return 2;
        return 0;
    }
}

/// <summary>Reads stdin to EOF with a hard cap, optionally copying the bytes to another stream (<c>--tee</c>).</summary>
public static class CappedReader
{
    public sealed class TooLargeException(long limit) : Exception("Input exceeds the limit.")
    {
        public long Limit { get; } = limit;
    }

    public static async Task<byte[]> ReadAllAsync(Stream input, long maxBytes, Stream? tee, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var n = await input.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n == 0) break;
            if (tee is not null)
            {
                await tee.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
                await tee.FlushAsync(ct).ConfigureAwait(false);
            }
            if (buffer.Length + n > maxBytes) throw new TooLargeException(maxBytes);
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }
}

/// <summary>Durations for <c>--expires</c>: <c>30m</c>, <c>12h</c>, <c>7d</c>, <c>2w</c>, or an ISO 8601 UTC time.</summary>
public static class ExpiryParser
{
    public static readonly TimeSpan Max = TimeSpan.FromDays(3650);

    public static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        value = value.Trim();
        if (value.Length < 2) return false;
        var unit = char.ToLowerInvariant(value[^1]);
        if (!long.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return false;
        double minutes = unit switch
        {
            'm' => n,
            'h' => n * 60.0,
            'd' => n * 1440.0,
            'w' => n * 10080.0,
            _ => -1,
        };
        if (minutes <= 0 || minutes > Max.TotalMinutes) return false;
        duration = TimeSpan.FromMinutes(minutes);
        return true;
    }

    /// <summary>Resolves <paramref name="value"/> to an absolute UTC time.</summary>
    public static DateTimeOffset Resolve(string value, DateTimeOffset now)
    {
        if (TryParseDuration(value, out var d)) return now + d;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            if (at <= now) throw CliException.Usage($"Expiry '{value}' is in the past.");
            return at;
        }
        throw CliException.Usage($"Invalid expiry '{value}'. Use a duration like 30m, 12h, 7d or 2w, or an ISO 8601 time.");
    }
}

public static class TextFormat
{
    /// <summary>Removes control and bidi-override characters from server- or user-supplied text before it reaches a
    /// terminal, so a title cannot inject escape sequences or reorder the line.</summary>
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            if (c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    public static string Utc(DateTimeOffset? value) =>
        value is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "-";
}
