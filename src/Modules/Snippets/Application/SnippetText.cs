using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Snippets.Application;

/// <summary>ARC-010 §3, §4.1 step 3, §4.3: pure text rules shared by create and edit.</summary>
public static partial class SnippetText
{
    public const int MaxTitleLength = 200;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex("^[a-z0-9][a-z0-9+#._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

    /// <summary>The UTF-8 bytes of the text, exactly as it will be stored and delivered: no trimming, no line-ending,
    /// BOM or Unicode normalisation. Refuses empty text, U+0000 (PostgreSQL <c>text</c> cannot hold it) and lone
    /// surrogates (not valid UTF-8).</summary>
    public static Result<byte[]> Encode(string? content)
    {
        if (string.IsNullOrEmpty(content)) return Error.Validation("The snippet text cannot be empty.");
        if (content.Contains('\0')) return Error.Validation("The snippet text cannot contain a NUL character.");
        try
        {
            return Result<byte[]>.Ok(Strict.GetBytes(content));
        }
        catch (EncoderFallbackException)
        {
            return Error.Validation("The snippet text is not valid UTF-8 (a lone surrogate was found).");
        }
    }

    /// <summary>Number of <c>\n</c> plus one when the text does not end with <c>\n</c>; <c>\r\n</c> counts once.</summary>
    public static int CountLines(string content)
    {
        var lines = 0;
        foreach (var c in content)
            if (c == '\n') lines++;
        return content.EndsWith('\n') ? Math.Max(lines, 1) : lines + 1;
    }

    /// <summary>Null stays null; otherwise lower-cased and checked against the column pattern.</summary>
    public static Result<string?> NormalizeLanguage(string? language)
    {
        if (language is null) return Result<string?>.Ok(null);
        var lower = language.Trim().ToLowerInvariant();
        return LanguagePattern().IsMatch(lower)
            ? Result<string?>.Ok(lower)
            : Error.Validation("The language is not a valid id (letters, digits and + # . _ -, at most 64 characters).");
    }

    /// <summary>Strips control characters and the bidi override/isolate characters (U+202A-202E, U+2066-2069),
    /// trims, and refuses more than 200 characters. An empty result is null.</summary>
    public static Result<string?> CleanTitle(string? title)
    {
        if (title is null) return Result<string?>.Ok(null);
        var sb = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (char.IsControl(c) || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩')) continue;
            sb.Append(c);
        }
        var clean = sb.ToString().Trim();
        if (clean.Length > MaxTitleLength) return Error.Validation($"The title can have at most {MaxTitleLength} characters.");
        return Result<string?>.Ok(clean.Length == 0 ? null : clean);
    }

    /// <summary>ARC-010 §4.4 <c>ansi=strip</c>: removes ECMA-48 CSI (<c>ESC [ ... final 0x40-0x7E</c>), OSC (<c>ESC ]</c>
    /// up to BEL or <c>ESC \</c>), other two-character <c>ESC x</c> sequences and C1 controls U+0080-U+009F.
    /// Tabs, CR and LF stay.</summary>
    public static string StripAnsi(string text)
    {
        if (text.IndexOf('\u001b') < 0 && !text.Any(c => c is >= '\u0080' and <= '\u009f')) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= '\u0080' and <= '\u009f') continue;
            if (c != '\u001b') { sb.Append(c); continue; }
            if (i + 1 >= text.Length) break;
            var next = text[++i];
            if (next == '[')
            {
                while (i + 1 < text.Length)
                {
                    var f = text[++i];
                    if (f is >= '@' and <= '~') break;
                }
            }
            else if (next == ']')
            {
                while (i + 1 < text.Length)
                {
                    var f = text[++i];
                    if (f == '\a') break;
                    if (f == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\') { i++; break; }
                }
            }
            // any other ESC x: the pair is dropped.
        }
        return sb.ToString();
    }

    /// <summary>
    /// ARC-010 §4.4 plus UTF-16 (BE-010): decodes a received body. <paramref name="charset"/> null or utf-8 is UTF-8
    /// (a BOM is kept as U+FEFF, text fidelity §4.3); utf-16, utf-16le and utf-16be are UTF-16, and a UTF-16 BOM
    /// (FF FE or FE FF) selects UTF-16 whatever the header says. A UTF-16 BOM is consumed as an encoding marker.
    /// Another charset, invalid sequences, an odd UTF-16 length or U+0000 is <c>unsupported-media-type</c>.
    /// </summary>
    public static Result<string> DecodeIngest(ReadOnlySpan<byte> body, string? charset)
    {
        var name = charset?.Trim().Trim('"').ToLowerInvariant();
        var bigEndian = false;
        var utf16 = false;
        if (name is null or "" or "utf-8" or "utf8") { }
        else if (name is "utf-16" or "utf-16le") utf16 = true;
        else if (name is "utf-16be") { utf16 = true; bigEndian = true; }
        else return Error.Of(ErrorCodes.UnsupportedMediaType, "Only UTF-8 and UTF-16 text is accepted.");

        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE) { utf16 = true; bigEndian = false; body = body[2..]; }
        else if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF) { utf16 = true; bigEndian = true; body = body[2..]; }

        try
        {
            string text;
            if (utf16)
            {
                if (body.Length % 2 != 0) return Error.Of(ErrorCodes.UnsupportedMediaType, "The UTF-16 body has an odd number of bytes.");
                text = new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true).GetString(body);
            }
            else
            {
                text = Strict.GetString(body);
            }
            return text.Contains('\0')
                ? Error.Of(ErrorCodes.UnsupportedMediaType, "The body contains NUL characters (binary input). Use a file upload instead.")
                : Result<string>.Ok(text);
        }
        catch (DecoderFallbackException)
        {
            return Error.Of(ErrorCodes.UnsupportedMediaType, "The body is not valid text in its declared encoding (binary input). Use a file upload instead.");
        }
        catch (ArgumentException)
        {
            return Error.Of(ErrorCodes.UnsupportedMediaType, "The body is not valid text in its declared encoding (binary input). Use a file upload instead.");
        }
    }

    public static byte[] Sha256(byte[] utf8) => SHA256.HashData(utf8);
}
