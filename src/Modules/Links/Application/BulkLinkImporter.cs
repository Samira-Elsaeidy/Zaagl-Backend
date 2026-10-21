using System.Text;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Application;

/// <summary>Creates up to 500 links from a CSV with a header row: destination (or url), slug, title, tags.</summary>
public sealed class BulkLinkImporter(LinkService links)
{
    public const int MaxRows = 500;

    public async Task<Result<BulkResult>> ImportAsync(Guid userId, Stream csv, CancellationToken ct)
    {
        List<List<string>> table;
        using (var reader = new StreamReader(csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            table = ParseCsv(await reader.ReadToEndAsync(ct));

        if (table.Count == 0) return Error.Validation("The CSV file is empty.");

        var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var urlCol = IndexOfAny(header, "destination", "url", "destination_url");
        if (urlCol < 0) return Error.Validation("The CSV needs a 'destination' (or 'url') column.");
        var slugCol = IndexOfAny(header, "slug", "custom_slug");
        var titleCol = IndexOfAny(header, "title");
        var tagsCol = IndexOfAny(header, "tags");

        var dataRows = table.Skip(1).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (dataRows.Count == 0) return Error.Validation("The CSV has no data rows.");
        if (dataRows.Count > MaxRows) return Error.Validation($"At most {MaxRows} links can be imported at once.");

        var results = new List<BulkRowResult>(dataRows.Count);
        for (var i = 0; i < dataRows.Count; i++)
        {
            var row = dataRows[i];
            var rowNumber = i + 2; // 1-based, header is row 1

            var input = new LinkInput(
                DestinationUrl: Cell(row, urlCol) ?? "",
                CustomSlug: Cell(row, slugCol),
                Title: Cell(row, titleCol),
                Tags: Cell(row, tagsCol)?.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var created = await links.CreateAsync(userId, input, ct);
            results.Add(created.IsSuccess
                ? new(rowNumber, true, created.Value!.Slug, null)
                : new(rowNumber, false, null, created.Error!.Message));
        }

        var ok = results.Count(r => r.Ok);
        return Result<BulkResult>.Ok(new(results.Count, ok, results.Count - ok, results));
    }

    private static int IndexOfAny(List<string> header, params string[] names) =>
        header.FindIndex(h => names.Contains(h));

    private static string? Cell(List<string> row, int col)
    {
        if (col < 0 || col >= row.Count) return null;
        var value = row[col].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Minimal RFC 4180 parser: quoted fields, escaped quotes, CRLF or LF line ends.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = [];
                    break;
                default: field.Append(c); break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
