namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>
/// Turns a (length, leased number) pair into a slug and back, and tells whether a candidate custom
/// alias collides with something the generator could ever produce (spec §4.5). Key rotation is out of
/// scope for this build: <see cref="KeyVersion"/> is always 1 (spec §5.3).
/// </summary>
public sealed class CodeGenerator(ICodePermutation permutation)
{
    public const short KeyVersion = 1;

    public string Encode(int length, long number) =>
        Base62.Encode(permutation.Permute(length, KeyVersion, number), length);

    /// <summary>Recovers the leased number from a code of this exact length. Null if not decodable at that length.</summary>
    public long? Decode(int length, string code)
    {
        if (code.Length != length) return null;
        if (!Base62.TryDecode(code, out var value)) return null;
        if (value < 0 || value >= CodeLengths.SpaceSize(length)) return null;
        return permutation.Unpermute(length, KeyVersion, value);
    }

    /// <summary>
    /// True when a string of generated shape could one day be produced by the generator (spec §4.5):
    /// it decodes, for its own length, to a number under that length's GENERATOR_CAP (2%). A custom
    /// alias must never be accepted when this is true. Returns false immediately - without ever
    /// calling <see cref="ICodePermutation.Unpermute"/> - for anything that cannot be generator shape:
    /// wrong length, a '-' or '_', or characters outside the base-62 alphabet.
    /// </summary>
    public bool IsGeneratorReachable(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        if (code.Length < CodeLengths.MinLength || code.Length > CodeLengths.MaxLength) return false;
        foreach (var c in code)
        {
            if (c is '-' or '_') return false;
        }
        if (!Base62.TryDecode(code, out var value)) return false;
        if (value < 0 || value >= CodeLengths.SpaceSize(code.Length)) return false;

        var number = permutation.Unpermute(code.Length, KeyVersion, value);
        return number < CodeLengths.GeneratorCap(code.Length);
    }
}
