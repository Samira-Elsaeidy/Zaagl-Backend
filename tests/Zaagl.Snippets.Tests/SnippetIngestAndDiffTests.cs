using System.Text;
using Zaagl.Modules.Snippets.Application;

namespace Zaagl.Snippets.Tests;

/// <summary>BE-009 (ARC-010 §10) and BE-010 (§4.4): the pure diff builder, ANSI stripping and body decoding.</summary>
public sealed class SnippetIngestAndDiffTests
{
    [Theory]
    [InlineData("\u001b[1;31mred\u001b[0m", "red")]
    [InlineData("a\u001b]0;window title\ab", "ab")]
    [InlineData("a\u001b]8;;http://x\u001b\\link\u001b]8;;\u001b\\b", "alinkb")]
    [InlineData("a\u001bMb", "ab")]
    [InlineData("x\u009b31my", "x31my")]
    [InlineData("tab\there\r\nnext", "tab\there\r\nnext")]
    [InlineData("trailing\u001b", "trailing")]
    [InlineData("unterminated\u001b[31", "unterminated")]
    public void StripAnsi_removes_escape_sequences_and_keeps_whitespace(string input, string expected) =>
        Assert.Equal(expected, SnippetText.StripAnsi(input));

    [Fact]
    public void Decode_utf8_keeps_a_bom_and_utf16_consumes_its_bom()
    {
        var utf8 = SnippetText.DecodeIngest([0xEF, 0xBB, 0xBF, 0x61], "utf-8");
        Assert.Equal("﻿a", utf8.Value);

        var le = SnippetText.DecodeIngest([0xFF, 0xFE, 0x61, 0x00], null);
        Assert.Equal("a", le.Value);
        var be = SnippetText.DecodeIngest([0xFE, 0xFF, 0x00, 0x61], "utf-8");
        Assert.Equal("a", be.Value);
        Assert.Equal("a", SnippetText.DecodeIngest([0x00, 0x61], "utf-16be").Value);
        Assert.Equal("a", SnippetText.DecodeIngest([0x61, 0x00], "UTF-16").Value);
    }

    [Theory]
    [InlineData("iso-8859-1")]
    [InlineData("windows-1252")]
    [InlineData("utf-32")]
    public void Decode_refuses_other_charsets(string charset) =>
        Assert.Equal("unsupported-media-type", SnippetText.DecodeIngest("x"u8, charset).Error!.Code);

    [Fact]
    public void Decode_refuses_lone_surrogates_in_utf16_and_odd_lengths()
    {
        Assert.Equal("unsupported-media-type", SnippetText.DecodeIngest([0x00, 0xD8, 0x61, 0x00], "utf-16le").Error!.Code); // lone high surrogate
        Assert.Equal("unsupported-media-type", SnippetText.DecodeIngest([0x61, 0x00, 0x62], "utf-16le").Error!.Code);
    }

    [Fact]
    public void Diff_unchanged_text_has_only_unchanged_rows()
    {
        var diff = SnippetDiff.Compute(1, 2, "a\nb", "a\nb", false);
        Assert.All(diff.Lines, r => Assert.Equal("unchanged", r.Left.Type));
        Assert.Equal(new(0, 0, 0), diff.Stats);
    }

    [Fact]
    public void Diff_handles_insert_delete_and_replace_blocks_with_correct_line_numbers()
    {
        var diff = SnippetDiff.Compute(1, 2, "a\nb\nc\nd", "a\nX\nY\nZ\nd\ne", false);
        // b,c replaced by X,Y,Z (2 modified + 1 inserted), then e inserted.
        Assert.Equal(new(2, 0, 2), diff.Stats);
        var rows = diff.Lines;
        Assert.Equal(new[] { "unchanged", "modified", "modified", "imaginary", "unchanged", "imaginary" }, rows.Select(r => r.Left.Type));
        Assert.Equal(new[] { "unchanged", "modified", "modified", "inserted", "unchanged", "inserted" }, rows.Select(r => r.Right.Type));
        Assert.Equal(new int?[] { 1, 2, 3, null, 4, null }, rows.Select(r => r.Left.No));
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6 }, rows.Select(r => r.Right.No));
    }

    [Fact]
    public void Diff_treats_crlf_and_lf_as_the_same_line_ending_in_pieces()
    {
        var diff = SnippetDiff.Compute(1, 2, "a\r\nb\r\n", "a\nb\n", false);
        Assert.Equal(new(0, 0, 0), diff.Stats);
    }

    [Fact]
    public void Diff_of_a_large_unrelated_pair_finishes_quickly()
    {
        var left = string.Join('\n', Enumerable.Range(0, 9_000).Select(i => "left " + i));
        var right = string.Join('\n', Enumerable.Range(0, 9_000).Select(i => "right " + i));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var diff = SnippetDiff.Compute(1, 2, left, right, false);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Equal(9_000, diff.Stats.Changed);
    }
}
