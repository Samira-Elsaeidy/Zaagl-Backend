using System.Text.Json;
using Zaagl.Modules.DeveloperTools.Application;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>
/// Phase 2 pen-test fix: bug-capsule URL redaction (docs/phase2/bug-capsules.md) used to miss array/bracket keys (<c>token[]=</c>,
/// <c>api_key[0]=</c>), <c>;</c>-delimited parameters and encoded keys. Pure unit tests over <see cref="CapsuleLogs"/>, no host.
/// </summary>
[Trait("Area", "P2-Capsules")]
public sealed class CapsuleRedactionTests
{
    [Theory]
    [InlineData("https://a.test/p?token[]=s3cr3t", "https://a.test/p?token[]=[redacted]")]
    [InlineData("https://a.test/p?api_key[0]=s3cr3t&api_key[1]=two", "https://a.test/p?api_key[0]=[redacted]&api_key[1]=[redacted]")]
    [InlineData("https://a.test/p?token%5B%5D=s3cr3t", "https://a.test/p?token%5B%5D=[redacted]")]
    [InlineData("https://a.test/p?auth[token]=s3cr3t&user[name]=dana", "https://a.test/p?auth[token]=[redacted]&user[name]=dana")]
    [InlineData("https://a.test/p?user[password]=hunter2", "https://a.test/p?user[password]=[redacted]")]
    [InlineData("https://a.test/p?a=1;token=s3cr3t;b=2", "https://a.test/p?a=1;token=[redacted];b=2")]
    [InlineData("https://a.test/p?a=1&session=x;sig=y", "https://a.test/p?a=1&session=[redacted];sig=[redacted]")]
    [InlineData("https://a.test/p?TOKEN=x&Api_Key=y", "https://a.test/p?TOKEN=[redacted]&Api_Key=[redacted]")]
    [InlineData("https://a.test/p?%74oken=x", "https://a.test/p?%74oken=[redacted]")]
    [InlineData("https://a.test/p?%2574oken=x", "https://a.test/p?%2574oken=[redacted]")]
    [InlineData("https://a.test/p?+token+=x", "https://a.test/p?+token+=[redacted]")]
    [InlineData("https://a.test/p?X-Amz-Signature=abc&X-Amz-Date=1", "https://a.test/p?X-Amz-Signature=[redacted]&X-Amz-Date=1")]
    [InlineData("https://a.test/p?page=2&sort=asc", "https://a.test/p?page=2&sort=asc")]
    [InlineData("https://a.test/p?tokenizer=on&keyboard=1", "https://a.test/p?tokenizer=on&keyboard=1")]
    [InlineData("https://a.test/p?token", "https://a.test/p?token")]
    [InlineData("https://a.test/p?token=x#frag", "https://a.test/p?token=[redacted]#[redacted]")]
    [InlineData("https://dana:hunter2@a.test/p?q=1", "https://[redacted]@a.test/p?q=1")]
    [InlineData("/relative?code=abc;state=1", "/relative?code=[redacted];state=1")]
    public void Sensitive_query_values_are_redacted(string input, string expected) =>
        Assert.Equal(expected, CapsuleLogs.RedactUrl(input));

    [Fact]
    public void Redaction_applies_inside_the_normalised_document()
    {
        using var doc = JsonDocument.Parse("""
            {"v":1,"network":[{"t":1,"method":"GET","url":"https://api.test/x?api_key[0]=s3cr3t;token=t0k","status":200}]}
            """);
        var result = CapsuleLogs.Normalize(doc.RootElement);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.DoesNotContain("s3cr3t", result.Value.Json);
        Assert.DoesNotContain("t0k", result.Value.Json);
    }

    [Fact]
    public void The_t_error_message_shows_a_readable_bound()
    {
        using var doc = JsonDocument.Parse("""{"v":1,"console":[{"t":-1,"level":"log","message":"x"}]}""");
        var result = CapsuleLogs.Normalize(doc.RootElement);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("E+", result.Error!.Message);
        Assert.DoesNotContain("1.79", result.Error.Message);
        Assert.Contains("0 or more", result.Error.Message);
    }
}
