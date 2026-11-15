using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Collaboration.Application;

/// <summary>Column types inferred from the non-empty cells of a column (docs/phase2/clipboard-tables.md).</summary>
public static class TableColumnTypes
{
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string String = "string";
    /// <summary>A nested JSON object or array, kept as compact JSON text.</summary>
    public const string Json = "json";
    /// <summary>Every cell empty.</summary>
    public const string Empty = "empty";
}

public sealed record TableColumn(string Name, string Type);

/// <summary>A normalised table: column names and types, and rows of JSON scalars (string, number, boolean or null) of exactly
/// <c>Columns.Count</c> cells each.</summary>
public sealed record ParsedTable(string SourceFormat, IReadOnlyList<TableColumn> Columns, IReadOnlyList<JsonNode?[]> Rows);

public sealed record TableParseLimits(int MaxRows, int MaxColumns, int MaxCellChars = TableParser.DefaultMaxCellChars, int MaxDepth = TableParser.DefaultMaxDepth);

/// <summary>
/// E2.3 clipboard transformers (docs/phase2/clipboard-tables.md): strict, bounded parsing of pasted JSON or CSV into a normalised table.
/// Nothing is ever evaluated: a CSV cell like <c>=HYPERLINK(...)</c> is text, stored and served as text (the CSV export neutralises it,
/// <see cref="TableExport"/>). JSON: <see cref="JsonDocument"/> with a depth limit, no comments, no trailing commas. CSV: RFC 4180
/// (quotes, doubled quotes, CRLF or LF; a quote inside an unquoted cell is literal), delimiter <c>,</c> <c>;</c> tab or <c>|</c> (detected from the first line when not given). Every
/// loop is bounded by the row, column and cell limits; NUL is refused.
/// </summary>
public static partial class TableParser
{
    public const int DefaultMaxCellChars = 10_000;
    public const int DefaultMaxDepth = 16;
    public const int MaxHeaderChars = 200;
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];

    public static Result<ParsedTable> Parse(string text, string? format, char? delimiter, bool header, TableParseLimits limits)
    {
        if (text.Contains('\0')) return Error.Validation("The input cannot contain NUL (U+0000) characters.");
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (string.IsNullOrWhiteSpace(text)) return Error.Validation("The input is empty.");
        var kind = (format ?? "auto").Trim().ToLowerInvariant();
        if (kind == "auto") kind = text.TrimStart() is ['[' or '{', ..] ? "json" : "csv";
        return kind switch
        {
            "json" => ParseJson(text, limits),
            "csv" => ParseCsv(text, delimiter, header, limits),
            _ => Error.Validation("format must be json, csv or auto."),
        };
    }

    // ---- JSON --------------------------------------------------------------------------------------------------------------------

    private static Result<ParsedTable> ParseJson(string text, TableParseLimits limits)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = limits.MaxDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false,
            });
        }
        catch (JsonException ex)
        {
            return Error.Validation($"The input is not valid JSON (line {ex.LineNumber + 1}).");
        }
        using (doc)
        {
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Array => FromArray(root, limits),
                JsonValueKind.Object => FromObject(root, limits),
                _ => Error.Validation("JSON input must be an array (of objects, arrays or values) or an object."),
            };
        }
    }

    /// <summary>An object is a two-column key/value table.</summary>
    private static Result<ParsedTable> FromObject(JsonElement root, TableParseLimits limits)
    {
        var rows = new List<JsonNode?[]>();
        foreach (var p in root.EnumerateObject())
        {
            if (rows.Count >= limits.MaxRows) return TooMany("rows", limits.MaxRows);
            var key = Cell(p.Name, limits);
            if (!key.IsSuccess) return key.Error!;
            var value = Scalar(p.Value, limits);
            if (!value.IsSuccess) return value.Error!;
            rows.Add([key.Value, value.Value]);
        }
        return Build("json", [("key", null), ("value", null)], rows);
    }

    private static Result<ParsedTable> FromArray(JsonElement root, TableParseLimits limits)
    {
        var count = root.GetArrayLength();
        if (count == 0) return Error.Validation("The JSON array is empty.");
        var objects = root.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object);
        var arrays = !objects && root.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Array);
        var rows = new List<JsonNode?[]>(Math.Min(count, limits.MaxRows));
        if (count > limits.MaxRows) return TooMany("rows", limits.MaxRows);

        if (objects)
        {
            // Columns: the union of the keys, in first-seen order.
            var names = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in root.EnumerateArray())
                foreach (var p in item.EnumerateObject())
                {
                    if (index.ContainsKey(p.Name)) continue;
                    if (names.Count >= limits.MaxColumns) return TooMany("columns", limits.MaxColumns);
                    index[p.Name] = names.Count;
                    names.Add(p.Name);
                }
            foreach (var item in root.EnumerateArray())
            {
                var row = new JsonNode?[names.Count];
                foreach (var p in item.EnumerateObject())
                {
                    var value = Scalar(p.Value, limits);
                    if (!value.IsSuccess) return value.Error!;
                    row[index[p.Name]] = value.Value;
                }
                rows.Add(row);
            }
            return Build("json", names.Select(n => (n, (string?)null)).ToList(), rows);
        }

        if (arrays)
        {
            var width = root.EnumerateArray().Max(e => e.GetArrayLength());
            if (width > limits.MaxColumns) return TooMany("columns", limits.MaxColumns);
            foreach (var item in root.EnumerateArray())
            {
                var row = new JsonNode?[width];
                var i = 0;
                foreach (var v in item.EnumerateArray())
                {
                    var value = Scalar(v, limits);
                    if (!value.IsSuccess) return value.Error!;
                    row[i++] = value.Value;
                }
                rows.Add(row);
            }
            return Build("json", Enumerable.Range(1, width).Select(n => ($"column {n}", (string?)null)).ToList(), rows);
        }

        // Mixed or scalar items: one column of values.
        foreach (var item in root.EnumerateArray())
        {
            var value = Scalar(item, limits);
            if (!value.IsSuccess) return value.Error!;
            rows.Add([value.Value]);
        }
        return Build("json", [("value", null)], rows);
    }

    /// <summary>A JSON value as a cell: scalars as themselves, nested objects/arrays as compact JSON text.</summary>
    private static Result<JsonNode?> Scalar(JsonElement e, TableParseLimits limits) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Result<JsonNode?>.Ok(null),
        JsonValueKind.True => Result<JsonNode?>.Ok(JsonValue.Create(true)),
        JsonValueKind.False => Result<JsonNode?>.Ok(JsonValue.Create(false)),
        JsonValueKind.Number => e.GetRawText().Length > 64
            ? Error.Validation("A number in the input is too long.")
            : Result<JsonNode?>.Ok(JsonNode.Parse(e.GetRawText())),
        JsonValueKind.String => Cell(e.GetString()!, limits),
        _ => Cell(e.GetRawText(), limits, json: true),
    };

    private static Result<JsonNode?> Cell(string value, TableParseLimits limits, bool json = false)
    {
        if (value.Contains('\0')) return Error.Validation("The input cannot contain NUL (U+0000) characters.");
        if (value.Length > limits.MaxCellChars) return Error.Validation($"A cell is at most {limits.MaxCellChars} characters.");
        if (json) value = CompactJson(value);
        return Result<JsonNode?>.Ok(JsonValue.Create(value));
    }

    private static string CompactJson(string raw)
    {
        using var d = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(d.RootElement);
    }

    // ---- CSV ---------------------------------------------------------------------------------------------------------------------

    private static Result<ParsedTable> ParseCsv(string text, char? delimiter, bool header, TableParseLimits limits)
    {
        var sep = delimiter ?? Detect(text);
        if (!Delimiters.Contains(sep)) return Error.Validation("delimiter must be one of , ; tab |.");
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var afterQuote = false;
        var i = 0;
        var maxRecords = limits.MaxRows + (header ? 1 : 0);

        Error? EndField()
        {
            if (record.Count >= limits.MaxColumns) return TooManyError("columns", limits.MaxColumns);
            record.Add(field.ToString());
            field.Clear();
            afterQuote = false;
            return null;
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false;
                    afterQuote = true;
                    i++;
                    continue;
                }
                field.Append(c);
                if (field.Length > limits.MaxCellChars) return Error.Validation($"A cell is at most {limits.MaxCellChars} characters.");
                i++;
                continue;
            }
            if (c == '"' && field.Length == 0)
            {
                if (afterQuote) return Error.Validation($"Row {records.Count + 1}: a quote right after a quoted cell.");
                inQuotes = true;
                i++;
                continue;
            }
            // A quote inside an unquoted cell (=HYPERLINK("x") pasted from a sheet) is kept as a literal character.
            if (c == sep)
            {
                if (EndField() is { } e) return e;
                i++;
                continue;
            }
            if (c is '\r' or '\n')
            {
                if (EndField() is { } e) return e;
                if (!(record.Count == 1 && record[0].Length == 0)) // skip blank lines
                {
                    if (records.Count >= maxRecords) return TooMany("rows", limits.MaxRows);
                    records.Add(record);
                }
                record = [];
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }
            if (afterQuote) return Error.Validation($"Row {records.Count + 1}: text after a closing quote.");
            field.Append(c);
            if (field.Length > limits.MaxCellChars) return Error.Validation($"A cell is at most {limits.MaxCellChars} characters.");
            i++;
        }
        if (inQuotes) return Error.Validation("The CSV ends inside a quoted cell.");
        if (field.Length > 0 || record.Count > 0 || afterQuote)
        {
            if (EndField() is { } e) return e;
            if (records.Count >= maxRecords) return TooMany("rows", limits.MaxRows);
            records.Add(record);
        }
        if (records.Count == 0) return Error.Validation("The CSV has no rows.");

        var width = records.Max(r => r.Count);
        List<(string, string?)> names;
        if (header)
        {
            var head = records[0];
            if (records.Skip(1).Any(r => r.Count > head.Count))
                return Error.Validation("A row has more cells than the header.");
            names = head.Select(h => (h, (string?)null)).ToList();
            records.RemoveAt(0);
            width = head.Count;
        }
        else
        {
            names = Enumerable.Range(1, width).Select(n => ($"column {n}", (string?)null)).ToList();
        }
        var rows = new List<JsonNode?[]>(records.Count);
        foreach (var r in records)
        {
            var row = new JsonNode?[width];
            for (var c = 0; c < r.Count && c < width; c++) row[c] = r[c].Length == 0 ? null : JsonValue.Create(r[c]);
            rows.Add(row);
        }
        return Build("csv", names, rows);
    }

    /// <summary>The delimiter that occurs most often outside quotes in the first line (comma when none does).</summary>
    private static char Detect(string text)
    {
        var counts = new Dictionary<char, int>();
        var inQuotes = false;
        foreach (var c in text.AsSpan(0, Math.Min(text.Length, 64 * 1024)))
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c is '\r' or '\n') break;
            else if (!inQuotes && Delimiters.Contains(c)) counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        return counts.Count == 0 ? ',' : counts.OrderByDescending(kv => kv.Value).ThenBy(kv => Array.IndexOf(Delimiters, kv.Key)).First().Key;
    }

    // ---- Shared ------------------------------------------------------------------------------------------------------------------

    private static Result<ParsedTable> Build(string source, IReadOnlyList<(string Name, string? Type)> names, List<JsonNode?[]> rows)
    {
        if (names.Count == 0) return Error.Validation("The table has no columns.");
        var cleaned = CleanNames(names.Select(n => n.Name).ToList());
        if (!cleaned.IsSuccess) return cleaned.Error!;
        var columns = new List<TableColumn>(names.Count);
        for (var c = 0; c < names.Count; c++) columns.Add(new TableColumn(cleaned.Value![c], Infer(rows, c)));
        return new ParsedTable(source, columns, rows);
    }

    /// <summary>Trimmed, control characters removed, at most <see cref="MaxHeaderChars"/>; empty becomes <c>column N</c>, duplicates get
    /// <c> (2)</c>, <c> (3)</c>….</summary>
    private static Result<List<string>> CleanNames(List<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var name = new string(raw[i].Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            if (name.Length > MaxHeaderChars) return Error.Validation($"A column name is at most {MaxHeaderChars} characters.");
            if (name.Length == 0) name = $"column {i + 1}";
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{name} ({n})";
            result.Add(unique);
        }
        return result;
    }

    [GeneratedRegex(@"^[+-]?\d{1,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerText();

    [GeneratedRegex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberText();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateText();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeText();

    /// <summary>True for a plain decimal or scientific number written in the invariant culture (used by the CSV export too).</summary>
    public static bool IsNumberText(string s) => s.Length <= 64 && NumberText().IsMatch(s);

    private static string Infer(List<JsonNode?[]> rows, int column)
    {
        string? type = null;
        foreach (var row in rows)
        {
            var cell = row[column];
            if (cell is null) continue;
            var t = TypeOf(cell);
            if (type is null) type = t;
            else if (type != t)
            {
                if ((type, t) is (TableColumnTypes.Integer, TableColumnTypes.Number) or (TableColumnTypes.Number, TableColumnTypes.Integer))
                    type = TableColumnTypes.Number;
                else if ((type, t) is (TableColumnTypes.Date, TableColumnTypes.DateTime) or (TableColumnTypes.DateTime, TableColumnTypes.Date))
                    type = TableColumnTypes.DateTime;
                else return TableColumnTypes.String;
            }
        }
        return type ?? TableColumnTypes.Empty;
    }

    private static string TypeOf(JsonNode cell)
    {
        var v = cell.AsValue();
        if (v.TryGetValue<bool>(out _)) return TableColumnTypes.Boolean;
        if (v.GetValueKind() == JsonValueKind.Number)
            return IntegerText().IsMatch(v.ToJsonString()) ? TableColumnTypes.Integer : TableColumnTypes.Number;
        var s = v.GetValue<string>().Trim();
        if (s.StartsWith('{') || s.StartsWith('[')) return LooksLikeJson(s) ? TableColumnTypes.Json : TableColumnTypes.String;
        if (IntegerText().IsMatch(s)) return TableColumnTypes.Integer;
        if (IsNumberText(s)) return TableColumnTypes.Number;
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return TableColumnTypes.Boolean;
        if (DateText().IsMatch(s) && System.DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return TableColumnTypes.Date;
        if (DateTimeText().IsMatch(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            return TableColumnTypes.DateTime;
        return TableColumnTypes.String;
    }

    private static bool LooksLikeJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = DefaultMaxDepth }); return true; }
        catch (JsonException) { return false; }
    }

    private static Error TooManyError(string what, int max) => Error.Of(ErrorCodes.InputTooLarge, $"The table can have at most {max} {what} on your plan.");

    private static Result<ParsedTable> TooMany(string what, int max) => TooManyError(what, max);
}

/// <summary>
/// The table as a downloadable document. CSV is RFC 4180 with every cell that could start a spreadsheet formula neutralised: a text cell
/// starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return is prefixed with a single quote (OWASP CSV injection);
/// a cell that is a plain number (for example <c>-12.5</c>) is not, since it cannot be a formula. Cells with the delimiter, a quote or a
/// line break are quoted, quotes doubled.
/// </summary>
public static class TableExport
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    public static string Csv(IReadOnlyList<TableColumn> columns, IEnumerable<JsonNode?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', columns.Select(c => Field(c.Name))).Append("\r\n");
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Field(Text(i < row.Length ? row[i] : null)));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public static string Json(IReadOnlyList<TableColumn> columns, IEnumerable<JsonNode?[]> rows)
    {
        var array = new JsonArray();
        foreach (var row in rows)
        {
            var o = new JsonObject();
            for (var i = 0; i < columns.Count; i++) o[columns[i].Name] = i < row.Length ? row[i]?.DeepClone() : null;
            array.Add(o);
        }
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Text(JsonNode? cell)
    {
        if (cell is null) return "";
        var v = cell.AsValue();
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        return v.ToJsonString();
    }

    /// <summary>One CSV field: formula-neutralised, then quoted when needed.</summary>
    public static string Field(string value)
    {
        if (value.Length > 0 && FormulaStarts.Contains(value[0]) && !TableParser.IsNumberText(value)) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n', ';', '\t']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
