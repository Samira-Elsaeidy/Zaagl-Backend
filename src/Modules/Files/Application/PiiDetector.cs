using System.Text;
using System.Text.RegularExpressions;

namespace Zaagl.Modules.Files.Application;

/// <summary>One detected piece of sensitive text: its kind and its position in the checked text (UTF-16 offsets). The matched text
/// itself is never returned or logged.</summary>
public sealed record PiiSpan(string Kind, int Start, int Length);

/// <summary>A word recognised by the client's OCR, with its box normalised to the displayed image (0..1).</summary>
public sealed record OcrWord(string Text, double X, double Y, double W, double H, int Line);

/// <summary>
/// E2.3 smart PII redaction guard, the server-side text check (docs/phase2/pii-redaction.md). Runs on text the client recognised (OCR in
/// the browser) or typed; pure, bounded (input caps in the caller, a match timeout on every pattern). Kinds:
/// <c>email</c>, <c>card</c> (13-19 digits, spaces or dashes allowed between digits, <b>Luhn checked</b>), <c>phone</c> (international
/// or common national formats, 9-15 digits, not a card), and keys/tokens: <c>aws-access-key</c>, <c>github-token</c>, <c>slack-token</c>,
/// <c>stripe-key</c>, <c>google-api-key</c>, <c>openai-key</c> (also <c>sk-ant-</c>), <c>jwt</c>, <c>private-key</c>, <c>bearer-token</c>,
/// <c>secret-assignment</c> (the value after <c>api_key=</c>, <c>token:</c>, <c>password=</c> …). False positives are expected and fine:
/// the owner reviews every box before anything is rendered.
/// </summary>
public static partial class PiiDetector
{
    public const string Email = "email";
    public const string Card = "card";
    public const string Phone = "phone";

    private const int TimeoutMs = 250;

    [GeneratedRegex(@"[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9-]{1,63}(?:\.[A-Za-z0-9-]{1,63})*\.[A-Za-z]{2,24}", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex EmailPattern();

    /// <summary>13-19 digits with at most one space or dash between two digits.</summary>
    [GeneratedRegex(@"(?<![\d-])\d(?:[ -]?\d){12,18}(?![\d-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CardPattern();

    /// <summary>+CC or 00CC international, or national groups like (555) 123-4567, 555.123.4567, 020 7946 0958.</summary>
    [GeneratedRegex(@"(?<![\w+])(?:(?:\+|00)[1-9]\d{0,2}[ .-]?)?(?:\(\d{1,4}\)[ .-]?)?\d{2,4}(?:[ .-]\d{2,4}){1,4}(?![\w])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PhonePattern();

    private static readonly (string Kind, Regex Pattern, int Group)[] Secrets =
    [
        ("private-key", new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("aws-access-key", new Regex(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("github-token", new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("slack-token", new Regex(@"\bxox[abposr]-[A-Za-z0-9-]{10,200}\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("stripe-key", new Regex(@"\b(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]{16,128}\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("google-api-key", new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("openai-key", new Regex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{20,200}\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]{8,2000}\.[A-Za-z0-9_-]{8,4000}\.[A-Za-z0-9_-]{8,2000}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 0),
        ("bearer-token", new Regex(@"\bBearer\s+([A-Za-z0-9._~+/-]{16,2000}=*)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 1),
        ("secret-assignment", new Regex(@"(?i)\b(?:api[_-]?key|apikey|secret|client[_-]?secret|access[_-]?token|auth[_-]?token|token|password|passwd|pwd|access[_-]?key)\b[""']?\s*[:=]\s*[""']?([^\s""',;]{6,500})",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs)), 1),
    ];

    /// <summary>Spans in <paramref name="text"/>, ordered by start; overlapping spans keep the first, most specific kind (secrets and cards
    /// before phones).</summary>
    public static IReadOnlyList<PiiSpan> Scan(string text)
    {
        var found = new List<PiiSpan>();
        void Add(string kind, int start, int length)
        {
            if (length <= 0 || found.Any(s => start < s.Start + s.Length && s.Start < start + length)) return;
            found.Add(new PiiSpan(kind, start, length));
        }
        try
        {
            foreach (var (kind, pattern, group) in Secrets)
                foreach (Match m in pattern.Matches(text))
                    Add(kind, m.Groups[group].Index, m.Groups[group].Length);
            foreach (Match m in EmailPattern().Matches(text)) Add(Email, m.Index, m.Length);
            foreach (Match m in CardPattern().Matches(text))
                if (IsCardNumber(m.Value)) Add(Card, m.Index, m.Length);
            foreach (Match m in PhonePattern().Matches(text))
            {
                var digits = m.Value.Count(char.IsAsciiDigit);
                if (digits is >= 9 and <= 15 && !LooksLikeDate(m.Value)) Add(Phone, m.Index, m.Length);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Bounded by design: whatever was found before the timeout is returned; the owner still reviews the image.
        }
        return found.OrderBy(s => s.Start).ToList();
    }

    /// <summary>13-19 digits (separators ignored) passing the Luhn checksum, not all the same digit.</summary>
    public static bool IsCardNumber(string candidate)
    {
        var digits = candidate.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19 || digits.All(d => d == digits[0])) return false;
        return Luhn(digits);
    }

    /// <summary>The Luhn (mod 10) checksum: from the right, every second digit doubled (minus 9 above 9); valid when the sum is a
    /// multiple of 10.</summary>
    public static bool Luhn(IReadOnlyList<int> digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Count; i++)
        {
            var d = digits[digits.Count - 1 - i];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    private static bool LooksLikeDate(string s) => DatePattern().IsMatch(s.Trim());

    [GeneratedRegex(@"^\d{4}[-./]\d{1,2}[-./]\d{1,2}$|^\d{1,2}[-./]\d{1,2}[-./]\d{2,4}$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex DatePattern();

    /// <summary>
    /// OCR words to redaction boxes: the words are joined line by line (one space between words, a line break between lines) so a card
    /// number split into four words is still found; every word overlapping a span becomes one box (its own rectangle, with a small margin).
    /// </summary>
    public static IReadOnlyList<(string Kind, int Word, double X, double Y, double W, double H)> Boxes(IReadOnlyList<OcrWord> words, double margin = 0.002)
    {
        var sb = new StringBuilder();
        var ranges = new List<(int Start, int End)>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) sb.Append(words[i].Line == words[i - 1].Line ? ' ' : '\n');
            var start = sb.Length;
            sb.Append(words[i].Text);
            ranges.Add((start, sb.Length));
        }
        var result = new List<(string, int, double, double, double, double)>();
        foreach (var span in Scan(sb.ToString()))
        {
            for (var i = 0; i < words.Count; i++)
            {
                var (s, e) = ranges[i];
                if (s >= span.Start + span.Length || e <= span.Start) continue;
                var w = words[i];
                var x = Math.Max(0, w.X - margin);
                var y = Math.Max(0, w.Y - margin);
                result.Add((span.Kind, i, x, y, Math.Min(1 - x, w.W + 2 * margin), Math.Min(1 - y, w.H + 2 * margin)));
            }
        }
        return result;
    }
}
