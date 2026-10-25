namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>
/// Per-length constants for the generator (spec §4.2, §4.5). Codes start at 5 characters and grow to
/// 10 as each length's seeded range fills. 10 is the maximum this build supports: 62^11 overflows
/// <see cref="long"/>. None of these numbers may change once codes exist for a length.
/// </summary>
public static class CodeLengths
{
    public const int MinLength = 5;
    public const int MaxLength = 10;

    // Smallest even bit width b with 2^b >= 62^L, for L = 5..10 (spec §5.1).
    private static readonly int[] BitWidths = [30, 36, 42, 48, 54, 60];

    public static int BitWidth(int length) => BitWidths[CheckRange(length) - MinLength];

    /// <summary>2^BitWidth(length): the Feistel permutation's domain for this length.</summary>
    public static long SpaceSize(int length) => 1L << BitWidth(length);

    /// <summary>62^length, computed with checked integer arithmetic (fits in <see cref="long"/> up to length 10).</summary>
    public static long PowerOf62(int length)
    {
        CheckRange(length);
        checked
        {
            var v = 1L;
            for (var i = 0; i < length; i++) v *= 62;
            return v;
        }
    }

    /// <summary>
    /// GENERATOR_CAP: 2% of 62^length, as exact integer arithmetic (floor), never floating point
    /// (spec table in §4.2 depends on exact values at the 10^17-10^18 range, where double precision
    /// would be unsafe). The boundary past which a custom alias is accepted because the generator can
    /// never reach it (spec §4.5). Must never increase once a length has started.
    /// </summary>
    public static long GeneratorCap(int length) => checked(PowerOf62(length) * 2 / 100);

    /// <summary>1% of 62^length: where the generator's initial seeded range ends (spec §4.2) - half of <see cref="GeneratorCap"/>.</summary>
    public static long RangeEndOnePercent(int length) => checked(PowerOf62(length) / 100);

    /// <summary>Lease block size per length (spec §4.2 seed table).</summary>
    public static int BlockSize(int length) => CheckRange(length) switch
    {
        5 => 1_000,
        6 or 7 => 10_000,
        _ => 100_000, // 8, 9, 10
    };

    private static int CheckRange(int length)
    {
        if (length < MinLength || length > MaxLength)
            throw new ArgumentOutOfRangeException(nameof(length), $"Code length must be between {MinLength} and {MaxLength}.");
        return length;
    }
}
