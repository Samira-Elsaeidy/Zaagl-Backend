using Zaagl.Modules.Links.Application;

namespace Zaagl.Links.Tests;

/// <summary>SSRF-style destination inputs. Every spelling of a loopback, private, link-local or metadata address is refused.</summary>
public sealed class UrlSafetySsrfTests
{
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://017700000001/")]
    [InlineData("http://127.1/")]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST./")]
    [InlineData("http://sub.localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[::127.0.0.1]/")]
    [InlineData("http://[::10.0.0.1]/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/")]
    [InlineData("http://[2002:7f00:1::]/")]
    [InlineData("http://[2002:a9fe:a9fe::1]/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://198.18.0.1/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://host.internal/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("http://user@example.com/")]
    [InlineData("http://:pw@example.com/")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//example.com/")]
    [InlineData("2130706433")]
    [InlineData("127.0.0.1")]
    [InlineData("")]
    [InlineData("   ")]
    public void Refused(string url)
    {
        Assert.False(UrlSafety.TryNormalize(url, ["zaa.gl"], out _, out var error), url);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c#d")]
    [InlineData("http://93.184.216.34/")]
    [InlineData("http://[2606:2800:220:1:248:1893:25c8:1946]/")]
    [InlineData("https://xn--bcher-kva.example/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://[2001:4860:4860::8888]/")]
    public void Public_destinations_are_accepted(string url)
    {
        Assert.True(UrlSafety.TryNormalize(url, ["zaa.gl"], out var normalized, out var error), $"{url}: {error}");
        Assert.NotEmpty(normalized);
    }

    [Fact]
    public void The_message_for_an_address_literal_is_the_private_loopback_one()
    {
        UrlSafety.TryNormalize("http://127.0.0.1/", [], out _, out var error);
        Assert.Equal("Private and loopback addresses are not allowed.", error);
    }
}
