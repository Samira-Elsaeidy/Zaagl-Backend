using Sqids;

namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>
/// Turns a sequence number into a short, non-sequential-looking slug of at least six characters.
/// The alphabet is lowercase letters and digits only, so two generated slugs can never differ just by letter
/// case (slugs are unique case-insensitively). It is shuffled once and must never change: existing links
/// would stop mapping to their numbers.
/// </summary>
public sealed class ShortCodeGenerator
{
    public const int MinLength = 6;
    public const string Alphabet = "k1e62mrsu7cwtjd9ph8yzboqg5a4v3xnlif0";

    private readonly SqidsEncoder<long> _encoder = new(new SqidsOptions
    {
        Alphabet = Alphabet,
        MinLength = MinLength,
    });

    public string Encode(long number) => _encoder.Encode(number);

    public long? Decode(string code)
    {
        var numbers = _encoder.Decode(code);
        return numbers.Count == 1 ? numbers[0] : null;
    }
}
