namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>A keyed bijection on [0, 62^length), per code length and key version (spec §5).</summary>
public interface ICodePermutation
{
    long Permute(int length, short keyVersion, long number);
    long Unpermute(int length, short keyVersion, long value);
}
