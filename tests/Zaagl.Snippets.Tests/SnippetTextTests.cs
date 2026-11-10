using System.Text;
using System.Text.Json;
using Zaagl.Modules.Snippets.Application;
using Zaagl.Modules.Snippets.Contracts;

namespace Zaagl.Snippets.Tests;

/// <summary>BE-008 (ARC-010 §3, §4.1, §4.3): the pure text rules and the wire shape of the render mode.</summary>
public sealed class SnippetTextTests
{
    [Fact]
    public void Encode_keeps_every_byte_and_counts_utf8_bytes()
    {
        var text = "﻿  café \U0001F600\r\n\ttab \n";
        var encoded = SnippetText.Encode(text);
        Assert.True(encoded.IsSuccess);
        Assert.Equal(new UTF8Encoding(false).GetBytes(text), encoded.Value);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), encoded.Value!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a\0b")]
    public void Encode_refuses_empty_nul_and_invalid_utf8(string? text) =>
        Assert.Equal("validation", SnippetText.Encode(text).Error!.Code);

    [Theory]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("\n", 1)]
    [InlineData("\n\n", 2)]
    [InlineData("a\n\nb", 3)]
    public void Line_count_is_newlines_plus_one_unless_the_text_ends_with_one(string text, int lines) =>
        Assert.Equal(lines, SnippetText.CountLines(text));

    [Theory]
    [InlineData("C#", "c#")]
    [InlineData("TypeScript", "typescript")]
    [InlineData(" js ", "js")]
    [InlineData(null, null)]
    public void Language_is_lower_cased(string? input, string? expected) =>
        Assert.Equal(expected, SnippetText.NormalizeLanguage(input).Value);

    [Theory]
    [InlineData("")]
    [InlineData("-js")]
    [InlineData("has space")]
    [InlineData("<script>")]
    public void An_invalid_language_is_refused(string input) =>
        Assert.False(SnippetText.NormalizeLanguage(input).IsSuccess);

    [Fact]
    public void Title_loses_control_and_bidi_override_characters_and_is_capped()
    {
        Assert.Equal("Hello", SnippetText.CleanTitle("  He‮ll⁦o\u0007\n ").Value);
        Assert.Null(SnippetText.CleanTitle("‮\n").Value);
        Assert.Null(SnippetText.CleanTitle(null).Value);
        Assert.True(SnippetText.CleanTitle(new string('x', 200)).IsSuccess);
        Assert.False(SnippetText.CleanTitle(new string('x', 201)).IsSuccess);
    }

    [Fact]
    public void Render_mode_is_a_lower_case_string_on_the_wire_and_a_number_is_refused()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal("\"terminal\"", JsonSerializer.Serialize(SnippetRenderMode.Terminal, web));
        Assert.Equal(SnippetRenderMode.Markdown, JsonSerializer.Deserialize<SnippetRenderMode>("\"markdown\"", web));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SnippetRenderMode>("2", web));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SnippetRenderMode>("\"html\"", web));
    }
}

public sealed class SnippetTextSurrogateTests
{
    [Fact]
    public void A_lone_surrogate_is_refused_but_a_pair_is_kept()
    {
        Assert.Equal("validation", SnippetText.Encode("a" + (char)0xD800 + "b").Error!.Code);
        Assert.Equal("validation", SnippetText.Encode("a" + (char)0xDC00 + "b").Error!.Code);
        Assert.True(SnippetText.Encode("a" + char.ConvertFromUtf32(0x1F600)).IsSuccess);
    }
}
