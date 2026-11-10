using DiffPlex;
using Zaagl.Modules.Snippets.Contracts;

namespace Zaagl.Modules.Snippets.Application;

/// <summary>
/// BE-009 (ARC-010 §10): a side-by-side line diff built from DiffPlex's line differ. Blocks are paired line by line:
/// the first min(deleted, inserted) lines of a block are <c>modified</c>, the surplus is <c>deleted</c> or
/// <c>inserted</c> with an <c>imaginary</c> filler opposite. No character-level (sub-piece) diff is computed, so the
/// cost stays linear in the output once the line diff is done. Text only: nothing here is HTML.
/// </summary>
public static class SnippetDiff
{
    public const string Unchanged = "unchanged", Inserted = "inserted", Deleted = "deleted", Modified = "modified", Imaginary = "imaginary";

    public static SnippetDiffDto Compute(int from, int to, string oldText, string newText, bool ignoreWhitespace)
    {
        var result = new Differ().CreateDiffs(oldText, newText, ignoreWhitespace, ignoreCase: false, new NewlineChunker());
        var rows = new List<SnippetDiffRow>(result.PiecesOld.Length + result.PiecesNew.Length);
        int added = 0, removed = 0, changed = 0;
        int a = 0, b = 0; // next unconsumed line in old and new

        SnippetDiffSide Side(string[] pieces, int index, string type) => new(index + 1, pieces[index], type);
        var filler = new SnippetDiffSide(null, "", Imaginary);

        void Unchange(int uptoA, int uptoB)
        {
            while (a < uptoA && b < uptoB)
            {
                rows.Add(new SnippetDiffRow(Side(result.PiecesOld, a, Unchanged), Side(result.PiecesNew, b, Unchanged)));
                a++; b++;
            }
        }

        foreach (var block in result.DiffBlocks)
        {
            Unchange(block.DeleteStartA, block.InsertStartB);
            var del = block.DeleteCountA;
            var ins = block.InsertCountB;
            var pairs = Math.Min(del, ins);
            for (var i = 0; i < pairs; i++)
                rows.Add(new SnippetDiffRow(Side(result.PiecesOld, a + i, Modified), Side(result.PiecesNew, b + i, Modified)));
            for (var i = pairs; i < del; i++)
                rows.Add(new SnippetDiffRow(Side(result.PiecesOld, a + i, Deleted), filler));
            for (var i = pairs; i < ins; i++)
                rows.Add(new SnippetDiffRow(filler, Side(result.PiecesNew, b + i, Inserted)));
            changed += pairs;
            removed += del - pairs;
            added += ins - pairs;
            a += del; b += ins;
        }
        Unchange(result.PiecesOld.Length, result.PiecesNew.Length);
        return new SnippetDiffDto(from, to, rows, new SnippetDiffStats(added, removed, changed));
    }
}

/// <summary>Splits on LF, CRLF or CR, keeping no terminators.</summary>
internal sealed class NewlineChunker : IChunker
{
    public string[] Chunk(string str)
    {
        if (str.Length == 0) return [];
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < str.Length; i++)
        {
            if (str[i] != '\n' && str[i] != '\r') continue;
            lines.Add(str[start..i]);
            if (str[i] == '\r' && i + 1 < str.Length && str[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < str.Length) lines.Add(str[start..]);
        return [.. lines];
    }
}
