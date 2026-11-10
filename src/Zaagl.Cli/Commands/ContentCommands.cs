using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Zaagl.Cli.Config;
using Zaagl.Cli.Http;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Commands;

/// <summary>CLI-007: <c>zaagl link &lt;url&gt;</c> creates a short link. Needs the links:write scope (device sessions have it).</summary>
public static class LinkCommand
{
    public static readonly CommandSpec Spec = new(
        "link", "Create a short link.", "zaagl link <url> [--slug <alias>] [--expires 7d] [--title <text>]",
        [
            new OptionSpec("slug", true, null, "Custom alias."),
            new OptionSpec("expires", true, null, "Expiry: 30m, 12h, 7d, 2w or an ISO 8601 UTC time."),
            new OptionSpec("title", true, null, "Link title."),
        ],
        RunAsync);

    private static async Task<int> RunAsync(CommandContext ctx, CancellationToken ct)
    {
        var o = ctx.Output;
        if (ctx.Args.Positionals.Count != 1) throw CliException.Usage("Usage: zaagl link <url> [--slug <alias>] [--expires 7d]");
        var destination = ctx.Args.Positionals[0].Trim();
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var dest) || dest.Scheme is not ("http" or "https") || destination.Length > 2048)
            throw CliException.Usage("The destination must be an absolute http or https URL (at most 2048 characters).");

        var now = ctx.Services.Time.GetUtcNow();
        var expires = ctx.Args.Get("expires") ?? ctx.Config.Get(ConfigKeys.DefaultExpiry);
        DateTimeOffset? expiresAt = expires is null ? null : ExpiryParser.Resolve(expires, now);

        var body = new LinkCreateRequestDto(destination, ctx.Args.Get("slug"), ctx.Args.Get("title"),
            expiresAt is null ? null : new LinkPolicyInputDto(expiresAt));
        var uri = ctx.Api.Resolve("api/links");
        var key = ApiClient.NewIdempotencyKey();
        var link = await ctx.Api.SendJsonAsync(
            () => ApiClient.Json(HttpMethod.Post, uri, body, CliJson.Context.LinkCreateRequestDto, key),
            CliJson.Context.LinkDto, authenticate: true, RetryMode.Idempotent, ct).ConfigureAwait(false);

        var url = link.ShortUrl ?? ShortUrlFallback(ctx.ApiBase, link.Slug);
        if (link.ShortUrl is null) o.Warn("The server did not return the short URL; the address above is derived from the API host.");

        if (o.Mode == OutputMode.Json)
            o.Json(new LinkResult(url, link.Slug, link.Id, link.DestinationUrl, link.Policy?.ExpiresAt ?? expiresAt), CliJson.Context.LinkResult);
        else
            o.Result(url);
        return ExitCodes.Ok;
    }

    /// <summary>Gap G6 (BE-LINKS-SHORTURL): until the API returns <c>shortUrl</c>, api.zaa.gl maps to zaa.gl.</summary>
    internal static string ShortUrlFallback(Uri apiBase, string slug)
    {
        var host = apiBase.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? apiBase.Host[4..] : apiBase.Host;
        var port = apiBase.IsDefaultPort ? "" : ":" + apiBase.Port.ToString(CultureInfo.InvariantCulture);
        return $"{apiBase.Scheme}://{host}{port}/{Uri.EscapeDataString(slug)}";
    }
}

/// <summary>CLI-008: <c>... | zaagl paste</c> sends stdin to <c>POST /api/snippets/raw</c> (contracts.md C9.1).</summary>
public static class PasteCommand
{
    /// <summary>Largest plan cap (Starter, C9.1). The real cap is read from /api/me/storage when available.</summary>
    public const long LargestKnownCap = 2_097_152;

    public static readonly CommandSpec Spec = new(
        "paste", "Share text from standard input as a snippet.",
        "<command> | zaagl paste [--lang <id>] [--title <text>] [--name <file.ext>] [--expires 7d] [--tee] [--keep-ansi]",
        [
            new OptionSpec("lang", true, null, "Language id, or 'auto' (default) to let the viewer detect it."),
            new OptionSpec("title", true, null, "Snippet title."),
            new OptionSpec("name", true, null, "File name; used as the title when --title is not given."),
            new OptionSpec("expires", true, null, "Expiry: 30m, 12h, 7d, 2w or an ISO 8601 UTC time."),
            new OptionSpec("tee", false, null, "Also copy the input to stderr so you still see it."),
            new OptionSpec("keep-ansi", false, null, "Keep terminal colour codes (rendered as a terminal snippet)."),
        ],
        RunAsync);

    private static async Task<int> RunAsync(CommandContext ctx, CancellationToken ct)
    {
        var o = ctx.Output;
        var console = ctx.Services.Console;
        if (ctx.Args.Positionals.Count > 0)
            throw CliException.Usage("'paste' reads standard input: use  <command> | zaagl paste   or   zaagl paste < file.txt");
        if (!console.IsInputRedirected)
            throw CliException.Usage("Nothing to paste: standard input is a terminal. Pipe something in, e.g.  dotnet test 2>&1 | zaagl paste --lang log");

        var lang = ctx.Args.Get("lang");
        if (lang is not null && lang != "auto" && !System.Text.RegularExpressions.Regex.IsMatch(lang, "^[A-Za-z0-9][A-Za-z0-9+#._-]{0,63}$"))
            throw CliException.Usage("Invalid --lang. Use a language id such as csharp, python or log.");
        var title = ctx.Args.Get("title") ?? ctx.Args.Get("name");
        if (title is { Length: > 200 }) throw CliException.Usage("The title can be at most 200 characters.");

        var now = ctx.Services.Time.GetUtcNow();
        var expires = ctx.Args.Get("expires") ?? ctx.Config.Get(ConfigKeys.DefaultExpiry);
        DateTimeOffset? expiresAt = expires is null ? null : ExpiryParser.Resolve(expires, now);

        // Fail on credentials before reading stdin (a long pipeline should not run to the end first), and learn the cap.
        var cap = await ResolveCapAsync(ctx, ct).ConfigureAwait(false);

        // The cap is on UTF-8 bytes; UTF-16 input is up to twice as many raw bytes, so read twice the cap and re-check.
        byte[] raw;
        Stream? tee = ctx.Args.Has("tee") ? console.OpenStandardError() : null;
        try
        {
            raw = await CappedReader.ReadAllAsync(console.OpenStandardInput(), cap * 2 + 4, tee, ct).ConfigureAwait(false);
        }
        catch (CappedReader.TooLargeException)
        {
            throw TooLarge(cap);
        }
        finally
        {
            tee?.Dispose();
        }

        var decoded = InputDecoder.Decode(raw);
        var text = decoded.Text;
        if (!ctx.Args.Has("keep-ansi")) text = AnsiStripper.Strip(text);
        if (text.Contains('\0'))
            throw new CliException(ExitCodes.Validation, "The input looks binary (it contains NUL characters). 'paste' shares text; use 'share' for files.", "binary-input");
        if (text.Trim().Length == 0) throw CliException.Usage("The input is empty; nothing was uploaded.");
        if (decoded.HadInvalidBytes) o.Warn("The input was not valid UTF-8; invalid bytes were replaced.");

        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > cap) throw TooLarge(cap);

        var query = new List<string>();
        if (lang is not null && lang != "auto") query.Add("lang=" + Uri.EscapeDataString(lang));
        if (title is not null) query.Add("title=" + Uri.EscapeDataString(title));
        if (expiresAt is { } at) query.Add("expiresAt=" + Uri.EscapeDataString(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        if (ctx.Args.Has("keep-ansi")) { query.Add("renderMode=terminal"); query.Add("ansi=keep"); }

        var uri = new Uri(ctx.Api.Resolve("api/snippets/raw") + (query.Count > 0 ? "?" + string.Join('&', query) : ""));
        var key = ApiClient.NewIdempotencyKey();
        o.Info($"Uploading {TextFormat.Bytes(bytes.Length)}...");
        var created = await ctx.Api.SendJsonAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(bytes) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
                return request;
            }, CliJson.Context.SnippetCreatedDto, authenticate: true, RetryMode.Idempotent, ct).ConfigureAwait(false);

        // Share URL = shortUrl + "#k=" + linkKey (C9.1). The key is returned once and never leaves this process otherwise.
        var url = created.ShortUrl + (string.IsNullOrEmpty(created.LinkKey) ? "" : "#k=" + created.LinkKey);
        if (o.Mode == OutputMode.Json)
            o.Json(new PasteResult(url, created.Code, created.ResourceId, created.SizeBytes, created.LineCount, created.ExpiresAt), CliJson.Context.PasteResult);
        else
            o.Result(url);
        return ExitCodes.Ok;
    }

    private static CliException TooLarge(long cap) =>
        new(ExitCodes.Validation, $"The input is larger than the {TextFormat.Bytes(cap)} snippet limit of your plan. Nothing was uploaded.", "snippet-too-large");

    private static async Task<long> ResolveCapAsync(CommandContext ctx, CancellationToken ct)
    {
        try
        {
            var uri = ctx.Api.Resolve("api/me/storage");
            var s = await ctx.Api.SendJsonAsync(() => ApiClient.Get(uri), CliJson.Context.StorageDto, true, RetryMode.Idempotent, ct).ConfigureAwait(false);
            return s.Limits?.MaxSnippetBytes is { } m and > 0 ? m : LargestKnownCap;
        }
        catch (CliException ex) when (ex.Code is "not-signed-in" or "session-ended")
        {
            throw;
        }
        catch (CliException)
        {
            return LargestKnownCap; // limits unreadable (scope, older server): the server enforces the real cap with 413
        }
    }
}

/// <summary>CLI-010: <c>zaagl ls</c>. Snippets and files come from <c>GET /api/resources</c> (cursor paging); links from
/// <c>GET /api/links</c> (page paging). <c>--json</c> prints JSON Lines: one row per line, nothing else.</summary>
public static class LsCommand
{
    public static readonly CommandSpec Spec = new(
        "ls", "List your shares.", "zaagl ls [--type link|snippet|file] [--limit 20] [--cursor <c> | --page <n>]",
        [
            new OptionSpec("type", true, null, "link, snippet or file (default: snippets and files)."),
            new OptionSpec("limit", true, null, "Rows per page, 1 to 100 (default 20)."),
            new OptionSpec("cursor", true, null, "Continue a snippet/file listing from a printed cursor."),
            new OptionSpec("page", true, null, "Page number for --type link (default 1)."),
        ],
        RunAsync);

    private static async Task<int> RunAsync(CommandContext ctx, CancellationToken ct)
    {
        var o = ctx.Output;
        if (ctx.Args.Positionals.Count > 0) throw CliException.Usage("'ls' takes no arguments.");
        var type = ctx.Args.Get("type");
        if (type is not (null or "link" or "snippet" or "file")) throw CliException.Usage("--type must be link, snippet or file.");
        var limit = 20;
        if (ctx.Args.Get("limit") is { } l && (!int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
            throw CliException.Usage("--limit must be a number from 1 to 100.");

        var rows = new List<LsRow>();
        string? next = null;
        if (type == "link")
        {
            if (ctx.Args.Has("cursor")) throw CliException.Usage("--cursor applies to snippets and files; use --page for links.");
            var page = 1;
            if (ctx.Args.Get("page") is { } p && (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
                throw CliException.Usage("--page must be a positive number.");
            var uri = ctx.Api.Resolve($"api/links?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={limit.ToString(CultureInfo.InvariantCulture)}");
            var result = await ctx.Api.SendJsonAsync(() => ApiClient.Get(uri), CliJson.Context.LinkPageDto, true, RetryMode.Idempotent, ct).ConfigureAwait(false);
            foreach (var item in result.Items)
                rows.Add(new LsRow("link", item.Link.Slug, item.Link.Title, item.Link.ShortUrl ?? LinkCommand.ShortUrlFallback(ctx.ApiBase, item.Link.Slug),
                    item.Link.IsActive ? "active" : "inactive", null, item.Clicks, item.Link.Policy?.ExpiresAt, item.Link.CreatedAt));
            if (page * result.PageSize < result.Total) next = "--page " + (page + 1).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (ctx.Args.Has("page")) throw CliException.Usage("--page applies to links; use --cursor for snippets and files.");
            var q = "limit=" + limit.ToString(CultureInfo.InvariantCulture);
            if (type is not null) q += "&kind=" + type;
            if (ctx.Args.Get("cursor") is { } c) q += "&cursor=" + Uri.EscapeDataString(c);
            var uri = ctx.Api.Resolve("api/resources?" + q);
            var result = await ctx.Api.SendJsonAsync(() => ApiClient.Get(uri), CliJson.Context.ResourcePageDto, true, RetryMode.Idempotent, ct).ConfigureAwait(false);
            foreach (var r in result.Items)
                rows.Add(new LsRow(r.Kind, r.Code, r.Title, r.ShortUrl, r.State, r.SizeBytes, null, r.ExpiresAt, r.CreatedAt));
            if (!string.IsNullOrEmpty(result.NextCursor)) next = "--cursor " + result.NextCursor;
        }

        if (o.Mode == OutputMode.Json)
            foreach (var row in rows) o.Json(row, CliJson.Context.LsRow);
        else if (o.Mode == OutputMode.Quiet)
            foreach (var row in rows) o.Result(row.Url);
        else if (rows.Count == 0)
            o.Result("Nothing here yet.");
        else
            PrintTable(o, rows);

        if (next is not null && o.Mode != OutputMode.Quiet) o.Console.Error.WriteLine("More: zaagl ls" + (type is null ? "" : " --type " + type) + " " + next);
        return ExitCodes.Ok;
    }

    private static void PrintTable(Output o, List<LsRow> rows)
    {
        string[][] cells = rows.Select(r => new[]
        {
            r.Kind, TextFormat.Safe(r.Title) is { Length: > 0 } t ? Truncate(t, 32) : "-", r.Url,
            r.SizeBytes is { } s ? TextFormat.Bytes(s) : r.Clicks is { } c ? c.ToString(CultureInfo.InvariantCulture) + " clicks" : "-",
            TextFormat.Utc(r.ExpiresAt),
        }).ToArray();
        string[] head = ["TYPE", "TITLE", "URL", "SIZE", "EXPIRES"];
        var widths = head.Select((h, i) => Math.Max(h.Length, cells.Max(c => c[i].Length))).ToArray();
        string Line(string[] c) => string.Join("  ", c.Select((v, i) => i == c.Length - 1 ? v : v.PadRight(widths[i])));
        o.Result(Line(head));
        foreach (var c in cells) o.Result(Line(c));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>CLI-010: non-secret settings. Secrets are refused; there is no way to store a token here.</summary>
public static class ConfigCommand
{
    public static readonly CommandSpec Spec = new(
        "config", "Read or change settings: get, set, list, unset.", "zaagl config get <key> | set <key> <value> | list | unset <key>", [], RunAsync);

    private static Task<int> RunAsync(CommandContext ctx, CancellationToken ct)
    {
        var o = ctx.Output;
        var p = ctx.Args.Positionals;
        var action = p.Count > 0 ? p[0] : throw CliException.Usage("Usage: zaagl config get <key> | set <key> <value> | list | unset <key>");

        switch (action)
        {
            case "list" when p.Count == 1:
                foreach (var key in ConfigKeys.All)
                {
                    var set = ctx.Config.Get(key);
                    var entry = new ConfigEntry(key, set ?? ConfigKeys.DefaultFor(key), set is null);
                    if (o.Mode == OutputMode.Json) o.Json(entry, CliJson.Context.ConfigEntry);
                    else o.Result($"{key} = {entry.Value ?? "(server default)"}{(entry.IsDefault ? "  (default)" : "")}");
                }
                return Task.FromResult(ExitCodes.Ok);

            case "get" when p.Count == 2:
                RequireKnown(p[1]);
                var current = ctx.Config.Get(p[1]);
                var value = current ?? ConfigKeys.DefaultFor(p[1]);
                if (o.Mode == OutputMode.Json) o.Json(new ConfigEntry(p[1], value, current is null), CliJson.Context.ConfigEntry);
                else o.Result(value ?? "");
                return Task.FromResult(ExitCodes.Ok);

            case "set" when p.Count == 3:
                if (ConfigKeys.Validate(p[1], p[2]) is { } problem) throw CliException.Usage(problem);
                ctx.Config.Values[p[1]] = p[2];
                ctx.Config.Save();
                if (o.Mode == OutputMode.Json) o.Json(new ConfigEntry(p[1], p[2], false), CliJson.Context.ConfigEntry);
                return Task.FromResult(ExitCodes.Ok);

            case "unset" when p.Count == 2:
                RequireKnown(p[1]);
                if (ctx.Config.Values.Remove(p[1])) ctx.Config.Save();
                if (o.Mode == OutputMode.Json) o.Json(new ConfigEntry(p[1], ConfigKeys.DefaultFor(p[1]), true), CliJson.Context.ConfigEntry);
                return Task.FromResult(ExitCodes.Ok);

            default:
                throw CliException.Usage("Usage: zaagl config get <key> | set <key> <value> | list | unset <key>");
        }
    }

    private static void RequireKnown(string key)
    {
        if (!ConfigKeys.IsKnown(key)) throw CliException.Usage($"Unknown key '{key}'. Known keys: {string.Join(", ", ConfigKeys.All)}.");
    }
}
