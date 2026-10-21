using Zaagl.Modules.Links.Application;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

public class ShortCodeGeneratorTests
{
    private readonly ShortCodeGenerator _codes = new();

    [Fact]
    public void Codes_are_at_least_six_characters()
    {
        foreach (var n in new long[] { 1, 1000, 123_456, long.MaxValue / 2 })
            Assert.True(_codes.Encode(n).Length >= 6);
    }

    [Fact]
    public void Codes_are_unique_and_decode_back()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (long n = 1000; n < 51_000; n++)
        {
            var code = _codes.Encode(n);
            Assert.Equal(n, _codes.Decode(code));
            Assert.True(seen.Add(code), $"duplicate code for {n}");
        }
    }

    [Fact]
    public void Codes_are_lower_case_so_they_cannot_clash_by_case()
    {
        for (long n = 1000; n < 51_000; n++)
            Assert.Matches("^[a-z0-9]+$", _codes.Encode(n));
    }

    [Fact]
    public void Consecutive_numbers_do_not_look_sequential()
    {
        Assert.NotEqual(_codes.Encode(1000)[..3], _codes.Encode(1001)[..3]);
    }
}

public class SlugRulesTests
{
    [Theory]
    [InlineData("menu", true)]
    [InlineData("My-Menu_2", true)]
    [InlineData("abc", true)]
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
