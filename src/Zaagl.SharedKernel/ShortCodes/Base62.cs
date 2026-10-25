namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>Base-62 encoding used for every short-link code (spec §5.1). Case-sensitive: 'A' != 'a'.</summary>
public static class Base62
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Encodes <paramref name="value"/> as exactly <paramref name="length"/> base-62 characters, left-padded with '0'.</summary>
    public static string Encode(long value, int length)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Value must not be negative.");
        if (length is <= 0 or > 16) throw new ArgumentOutOfRangeException(nameof(length));

        Span<char> buffer = stackalloc char[length];
        var v = value;
        for (var i = length - 1; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(v % 62)];
            v /= 62;
        }
        if (v != 0) throw new ArgumentOutOfRangeException(nameof(value), $"Value does not fit in {length} base-62 characters.");
        return new string(buffer);
    }

    /// <summary>Non-throwing decode. False on any character outside the alphabet or an empty span.</summary>
    public static bool TryDecode(ReadOnlySpan<char> code, out long value)
    {
        value = 0;
        if (code.Length == 0) return false;
        foreach (var c in code)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0)
            {
                value = 0;
                return false;
            }
            value = value * 62 + digit;
        }
        return true;
    }
}
