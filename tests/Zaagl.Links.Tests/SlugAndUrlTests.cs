using Zaagl.Modules.Links.Application;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

public class Base62Tests
{
    [Theory]
    [InlineData(0, 5, "00000")]
    [InlineData(61, 5, "0000z")]
    [InlineData(62, 5, "00010")]
    public void Encodes_with_padding(long value, int length, string expected) =>
        Assert.Equal(expected, Base62.Encode(value, length));

    [Fact]
    public void Round_trips() => RoundTripAll(0, 500_000);

    [Fact]
    public void Overflow_throws() => Assert.Throws<ArgumentOutOfRangeException>(() => Base62.Encode(62 * 62, 2));

    [Fact]
    public void Rejects_invalid_characters()
    {
        Assert.False(Base62.TryDecode("ab!c", out _));
        Assert.False(Base62.TryDecode("", out _));
    }

    [Fact]
    public void Case_sensitive()
    {
        Base62.TryDecode("A", out var upper);
        Base62.TryDecode("a", out var lower);
        Assert.NotEqual(upper, lower);
    }

    private static void RoundTripAll(long start, long count)
    {
        for (var v = start; v < start + count; v++)
        {
            var code = Base62.Encode(v, 6);
            Assert.True(Base62.TryDecode(code, out var back));
            Assert.Equal(v, back);
        }
    }
}

public class CodeLengthsTests
{
    [Theory]
    [InlineData(5, 30)]
    [InlineData(6, 36)]
    [InlineData(7, 42)]
    [InlineData(8, 48)]
    [InlineData(9, 54)]
    [InlineData(10, 60)]
    public void Bit_width_is_smallest_even_bits_covering_62_to_the_length(int length, int expectedBits)
    {
        Assert.Equal(expectedBits, CodeLengths.BitWidth(length));
        var n = CodeLengths.PowerOf62(length);
        Assert.True((1L << expectedBits) >= n);
        Assert.True((1L << (expectedBits - 2)) < n); // one even step down must already be insufficient
    }

    [Theory]
    [InlineData(5, 916_132_832)]
    [InlineData(6, 56_800_235_584)]
    [InlineData(7, 3_521_614_606_208)]
    [InlineData(8, 218_340_105_584_896)]
    [InlineData(9, 13_537_086_546_263_552)]
    [InlineData(10, 839_299_365_868_340_224)]
    public void Power_of_62_matches_spec_table(int length, long expected) =>
        Assert.Equal(expected, CodeLengths.PowerOf62(length));

    [Theory]
    [InlineData(5, 18_322_656)]
    [InlineData(6, 1_136_004_711)]
    [InlineData(7, 70_432_292_124)]
    [InlineData(8, 4_366_802_111_697)]
    [InlineData(9, 270_741_730_925_271)]
    [InlineData(10, 16_785_987_317_366_804)]
    public void Generator_cap_is_two_percent_matching_spec_table(int length, long expected) =>
        Assert.Equal(expected, CodeLengths.GeneratorCap(length));

    [Theory]
    [InlineData(5, 9_161_328)]
    [InlineData(6, 568_002_355)]
    [InlineData(7, 35_216_146_062)]
    [InlineData(8, 2_183_401_055_848)]
    [InlineData(9, 135_370_865_462_635)]
    [InlineData(10, 8_392_993_658_683_402)]
    public void Range_end_one_percent_matches_spec_table(int length, long expected)
    {
        // NOT exactly half of GeneratorCap: floor(N/100)*2 can differ from floor(N*2/100) by 1 due to
        // independent rounding (e.g. length 8: floor(N/100)*2 = ...696, floor(N*2/100) = ...697). The
        // spec table gives both columns as independently exact values, not as a 2x relationship.
        Assert.Equal(expected, CodeLengths.RangeEndOnePercent(length));
    }

    [Theory]
    [InlineData(5, 1_000)]
    [InlineData(6, 10_000)]
    [InlineData(7, 10_000)]
    [InlineData(8, 100_000)]
    [InlineData(9, 100_000)]
    [InlineData(10, 100_000)]
    public void Block_size_matches_spec_table(int length, int expected) =>
        Assert.Equal(expected, CodeLengths.BlockSize(length));

    [Fact]
    public void Rejects_lengths_outside_5_to_10()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeLengths.BitWidth(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeLengths.BitWidth(11));
    }
}

public class FeistelPermutationTests
{
    private readonly FeistelPermutation _perm = new("test-key-0123456789");

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Round_trips_edge_cases_per_length(int length)
    {
        foreach (var n in new[] { 0L, 1L, CodeLengths.PowerOf62(length) - 1, CodeLengths.GeneratorCap(length) })
            Assert.Equal(n, _perm.Unpermute(length, 1, _perm.Permute(length, 1, n)));
    }

    [Fact]
    public void Random_round_trips_per_length()
    {
        var rnd = new Random(42);
        for (var length = 5; length <= 10; length++)
        {
            var max = CodeLengths.PowerOf62(length);
            for (var i = 0; i < 100_000; i++)
            {
                var n = (long)(rnd.NextDouble() * max);
                Assert.Equal(n, _perm.Unpermute(length, 1, _perm.Permute(length, 1, n)));
            }
        }
    }

    [Fact]
    public void Is_a_bijection_on_a_small_domain()
    {
        // Exhaustive test on every value of a small length-equivalent domain: a Feistel network with
        // the same (l, r) split logic, just evaluated here at length 5's bit width restricted to a
        // small 62^3-sized slice, confirms no two inputs collide.
        const int length = 5;
        var n = 62 * 62 * 62; // a small slice of length 5's actual space, not a separate domain
        var seen = new HashSet<long>();
        for (long v = 0; v < n; v++)
        {
            var y = _perm.Permute(length, 1, v);
            Assert.True(seen.Add(y), $"collision: {v} produced {y} which was already seen");
            Assert.Equal(v, _perm.Unpermute(length, 1, y));
        }
    }

    [Fact]
    public void Rejects_values_outside_the_length_space()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _perm.Permute(5, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _perm.Permute(5, 1, CodeLengths.SpaceSize(5)));
    }

    [Fact]
    public void Different_keys_permute_differently() =>
        Assert.NotEqual(_perm.Permute(7, 1, 12345), new FeistelPermutation("a-completely-different-key").Permute(7, 1, 12345));
}

public class CodeGeneratorTests
{
    private readonly CodeGenerator _codes = new(new FeistelPermutation("test-key-0123456789"));

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    public void Codes_are_exactly_the_requested_length(int length)
    {
        foreach (var n in new[] { 0L, 1L, 1000L })
            Assert.Equal(length, _codes.Encode(length, n).Length);
    }

    [Fact]
    public void Codes_are_unique_and_decode_back_for_a_length()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (long n = 1000; n < 21_000; n++)
        {
            var code = _codes.Encode(7, n);
            Assert.Equal(n, _codes.Decode(7, code));
            Assert.True(seen.Add(code), $"duplicate code for {n}");
        }
    }

    [Fact]
    public void Codes_use_only_the_base62_alphabet()
    {
        for (long n = 1000; n < 21_000; n++)
            Assert.Matches("^[0-9A-Za-z]+$", _codes.Encode(7, n));
    }

    [Fact]
    public void Consecutive_numbers_do_not_look_sequential() =>
        Assert.NotEqual(_codes.Encode(7, 1000)[..3], _codes.Encode(7, 1001)[..3]);

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Numbers_under_the_seeded_range_are_all_generator_reachable(int length)
    {
        var rnd = new Random(7);
        var rangeEnd = CodeLengths.RangeEndOnePercent(length);
        for (var i = 0; i < 10_000; i++)
        {
            var n = (long)(rnd.NextDouble() * rangeEnd);
            var code = _codes.Encode(length, n);
            Assert.True(_codes.IsGeneratorReachable(code), $"length={length} n={n} code={code} should be reachable");
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    public void Numbers_at_or_above_the_cap_are_not_generator_reachable(int length)
    {
        var cap = CodeLengths.GeneratorCap(length);
        var rnd = new Random(11);
        var max = CodeLengths.PowerOf62(length);
        for (var i = 0; i < 2_000; i++)
        {
            var n = cap + (long)(rnd.NextDouble() * (max - cap));
            if (n >= max) continue;
            var code = _codes.Encode(length, n);
            if (SlugRules.IsReserved(code)) continue; // a reserved word would be rejected for a different reason
            Assert.False(_codes.IsGeneratorReachable(code), $"length={length} n={n} code={code} should not be reachable");
        }
    }

    [Fact]
    public void Disqualified_shapes_never_call_unpermute()
    {
        var spy = new CountingPermutation();
        var codes = new CodeGenerator(spy);

        Assert.False(codes.IsGeneratorReachable("ab-c"));   // contains '-'
        Assert.False(codes.IsGeneratorReachable("ab_c"));   // contains '_'
        Assert.False(codes.IsGeneratorReachable("abcd"));   // length 4, under the 5 minimum
        Assert.False(codes.IsGeneratorReachable(new string('a', 11))); // length 11, over the 10 maximum
        Assert.Equal(0, spy.UnpermuteCalls);

        codes.IsGeneratorReachable("abcde"); // length 5, plain base62: this one SHOULD call Unpermute
        Assert.Equal(1, spy.UnpermuteCalls);
    }

    private sealed class CountingPermutation : ICodePermutation
    {
        public int UnpermuteCalls;
        public long Permute(int length, short keyVersion, long number) => number;
        public long Unpermute(int length, short keyVersion, long value)
        {
            UnpermuteCalls++;
            return value;
        }
    }
}

public class SlugRulesTests
{
    [Theory]
    [InlineData("menu", true)]
    [InlineData("My-Menu_2", true)]
    [InlineData("abcd", true)]
    [InlineData("abc", false)] // 3 characters: below the spec's 4-character minimum (§4.5 rule 4)
    [InlineData("ab", false)]
    [InlineData("-menu", false)]
    [InlineData("has space", false)]
    [InlineData("dot.dot", false)]
    [InlineData("é-menu", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_custom_slug_format(string? slug, bool valid) =>
        Assert.Equal(valid, SlugRules.IsValidFormat(slug));

    [Fact]
    public void Rejects_slugs_longer_than_64()
    {
        Assert.True(SlugRules.IsValidFormat(new string('a', 64)));
        Assert.False(SlugRules.IsValidFormat(new string('a', 65)));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData("unlock")]
    [InlineData("health")]
    [InlineData("scalar")]
    public void Reserved_slugs_are_case_insensitive(string slug) => Assert.True(SlugRules.IsReserved(slug));

    [Fact]
    public void Slug_key_is_lower_case() => Assert.Equal("abcdef", SlugRules.Key("AbCdEf"));
}

public class UrlSafetyTests
{
    private static readonly string[] Own = ["zaa.gl", "www.zaa.gl"];

    private static bool Try(string? url, out string normalized) =>
        UrlSafety.TryNormalize(url, Own, out normalized, out _);

    [Theory]
    [InlineData("https://example.com/path?x=1")]
    [InlineData("http://example.com")]
    [InlineData("  https://example.com/a  ")]
    [InlineData("https://sub.example.co.uk:8443/a#frag")]
    [InlineData("https://8.8.8.8/")]
    public void Accepts_public_http_urls(string url) => Assert.True(Try(url, out _));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("example.com")]
    [InlineData("//example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_non_http_or_relative_urls(string? url) => Assert.False(Try(url, out _));

    [Theory]
    [InlineData("http://localhost/admin")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.4.4/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://db.internal/")]
    public void Rejects_local_and_private_hosts(string url) => Assert.False(Try(url, out _));

    [Fact]
    public void Rejects_embedded_credentials() => Assert.False(Try("https://user:pass@example.com", out _));

    [Theory]
    [InlineData("https://zaa.gl/abc")]
    [InlineData("https://ZAA.GL/abc")]
    [InlineData("https://www.zaa.gl/")]
    public void Rejects_links_pointing_back_at_zaagl(string url) => Assert.False(Try(url, out _));

    [Fact]
    public void Rejects_overlong_urls() =>
        Assert.False(Try("https://example.com/" + new string('a', UrlSafety.MaxLength), out _));

    [Fact]
    public void Normalizes_to_absolute_uri()
    {
        Assert.True(Try(" https://Example.com ", out var normalized));
        Assert.Equal("https://example.com/", normalized);
    }
}
