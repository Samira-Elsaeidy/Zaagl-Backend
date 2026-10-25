using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>
/// A balanced, 10-round keyed Feistel network per code length (spec §5.1). Exactly reversible by
/// <see cref="Unpermute"/>; a bijection on [0, 2^BitWidth(length)), narrowed to [0, 62^length) by
/// cycle-walking. The method (10-round Feistel, HMAC-SHA256, HKDF-derived per-length subkeys) is not a
/// secret - only the master key is. The master key must never change once any codes have been issued
/// for a length: a new key reshuffles the whole space, so an old leased number could come out as a
/// code already given to someone else. Store it like a password, never in source control (spec §5.3).
/// </summary>
public sealed class FeistelPermutation : ICodePermutation
{
    private const int Rounds = 10;
    private const int MaxCycleWalkIterations = 10_000;

    private readonly byte[] _ikm;
    private readonly ConcurrentDictionary<(int Length, short KeyVersion), byte[]> _subkeys = new();

    public FeistelPermutation(byte[] key)
    {
        if (key is null || key.Length == 0) throw new ArgumentException("A scramble key is required.", nameof(key));
        _ikm = key;
    }

    public FeistelPermutation(string key)
        : this(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("A scramble key is required.", nameof(key))
            : key))
    {
    }

    public long Permute(int length, short keyVersion, long number)
    {
        var n = CodeLengths.SpaceSize(length);
        if (number < 0 || number >= n) throw new ArgumentOutOfRangeException(nameof(number));
        return CycleWalk(length, keyVersion, number, forward: true);
    }

    public long Unpermute(int length, short keyVersion, long value)
    {
        var n = CodeLengths.SpaceSize(length);
        if (value < 0 || value >= n) throw new ArgumentOutOfRangeException(nameof(value));
        return CycleWalk(length, keyVersion, value, forward: false);
    }

    /// <summary>Re-applies the Feistel round until the result lands inside 62^length (spec §5.1). Expected 1.17-1.37 iterations.</summary>
    private long CycleWalk(int length, short keyVersion, long x, bool forward)
    {
        var n62 = CodeLengths.PowerOf62(length);
        var y = FeistelOnce(length, keyVersion, x, forward);
        var iterations = 1;
        while (y >= n62)
        {
            if (++iterations > MaxCycleWalkIterations)
                throw new InvalidOperationException(
                    $"Cycle walking exceeded {MaxCycleWalkIterations} iterations for length {length}; the permutation is broken.");
            y = FeistelOnce(length, keyVersion, y, forward);
        }
        return y;
    }

    private long FeistelOnce(int length, short keyVersion, long x, bool forward)
    {
        var h = CodeLengths.BitWidth(length) / 2;
        var mask = (1L << h) - 1;
        var l = (x >> h) & mask;
        var r = x & mask;
        var subkey = GetSubkey(length, keyVersion);

        if (forward)
        {
            for (var i = 0; i < Rounds; i++)
            {
                var f = Prf(subkey, i, r) & mask;
                (l, r) = (r, l ^ f);
            }
        }
        else
        {
            for (var i = Rounds - 1; i >= 0; i--)
            {
                var f = Prf(subkey, i, l) & mask;
                (l, r) = (r ^ f, l);
            }
        }

        return (l << h) | r;
    }

    /// <summary>HKDF.DeriveKey(SHA256, masterKey, 32, salt: "zaagl/codes", info: "perm/len{L}") - spec §5.1. Cached per (length, keyVersion).</summary>
    private byte[] GetSubkey(int length, short keyVersion) =>
        _subkeys.GetOrAdd((length, keyVersion), key => HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _ikm,
            outputLength: 32,
            salt: Encoding.UTF8.GetBytes("zaagl/codes"),
            info: Encoding.UTF8.GetBytes($"perm/len{key.Length}")));

    /// <summary>PRF(i, r) = first 8 bytes, big-endian, of HMAC-SHA256(subkey, [ (byte)i ][ r as 8-byte big-endian ]) - spec §5.1.</summary>
    private static long Prf(byte[] subkey, int round, long r)
    {
        Span<byte> message = stackalloc byte[9];
        message[0] = (byte)round;
        BinaryPrimitives.WriteUInt64BigEndian(message[1..], (ulong)r);

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(subkey, message, hash);

        return (long)BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
