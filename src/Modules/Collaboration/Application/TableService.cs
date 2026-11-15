using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Collaboration.Domain;
using Zaagl.Modules.Collaboration.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Collaboration.Application;

/// <summary>Who creates a table: an account, or a guest (derived key) with the PD-14 counter callbacks of the endpoint layer.</summary>
public sealed record TableCreator(Guid? UserId, byte[]? GuestCreatorKey, CreatedVia Via,
    Func<CancellationToken, Task<bool>>? TryAdmit = null, Func<Task>? Refund = null)
{
    public bool IsGuest => UserId is null;
}

/// <summary>
/// E2.3 instant clipboard transformers (docs/phase2/clipboard-tables.md). Pasted JSON or CSV is parsed strictly (<see cref="TableParser"/>)
/// into a normalised table (columns with inferred types, rows of scalars) stored as <c>jsonb</c> in <c>collab.tables</c>, behind a content
/// link of kind <c>table</c> (viewer <c>app.zaa.gl/t/&lt;code&gt;</c>). Guests may create one (CAPTCHA <c>guest-table</c>, PD-14 counters).
/// Create order (addendum §2.2): plan, size, parse, guest counters, register (stored bytes reserved), row, link (C1), bind, ready.
/// </summary>
public sealed class TableService(
    CollabDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    ToolOwnership ownership,
    TimeProvider time,
    ILogger<TableService> logger)
{
    public const int DefaultPageRows = 1000;
    public const int MaxPageRows = 5000;

    public async Task<Result<ToolLinkCreatedDto>> CreateAsync(TableCreator actor, CreateTableCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = actor.IsGuest ? plans.Guest : await plans.GetForUserAsync(actor.UserId!.Value, ct);
        var tools = plan.ToolsOrNone;
        if (!tools.TablesEnabled || tools.TableMaxInputBytes < 1)
            return actor.IsGuest ? Error.Unauthorized("Sign in to create a table link.") : Error.PlanRequired("Table links are not available on your plan.");
        if (command.Text is null) return Error.Validation("text is required (the pasted JSON or CSV).");
        if (Encoding.UTF8.GetByteCount(command.Text) > tools.TableMaxInputBytes)
            return Error.Of(ErrorCodes.InputTooLarge, $"The pasted data can be at most {tools.TableMaxInputBytes} bytes on your plan.");

        char? delimiter = null;
        if (!string.IsNullOrEmpty(command.Delimiter))
        {
            delimiter = command.Delimiter switch { "," => ',', ";" => ';', "\t" or "tab" => '\t', "|" => '|', _ => '\0' };
            if (delimiter == '\0') return Error.Validation("delimiter must be one of , ; tab |.");
        }
        var parsed = TableParser.Parse(command.Text, command.Format, delimiter, command.Header ?? true,
            new TableParseLimits(tools.TableMaxRows, tools.TableMaxColumns));
        if (!parsed.IsSuccess) return parsed.Error!;
        var table = parsed.Value!;

        var title = ToolOwnership.Label(command.Title, 200, "title");
        if (!title.IsSuccess) return title.Error!;
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (actor.IsGuest && command.Public) return Error.Validation("Guest content cannot be public.");
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");
        var expiry = ToolOwnership.Expiry(command.ExpiresAt, now, plan.Limits.RetentionDays, []);
        if (!expiry.IsSuccess) return expiry.Error!;

        var document = Serialize(table);
        var bytes = Encoding.UTF8.GetByteCount(document);

        // Guest counters (PD-14, shared with snippets, uploads and the other tools): after every check that can refuse for free.
        var admitted = false;
        if (actor.IsGuest && actor.TryAdmit is { } admit)
        {
            if (!await admit(ct)) return Error.Of(ErrorCodes.Quota, "Guest limit reached (30 per hour, 60 per day). Sign in or try again later.");
            admitted = true;
        }

        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Table, new ContentOwner(actor.UserId, actor.UserId is null ? actor.GuestCreatorKey : null), bytes, expiry.Value,
            title.Value, BurnAfterDownload: false, actor.Via), ct);
        if (!registered.IsSuccess)
        {
            if (admitted && actor.Refund is { } refund) await refund();
            return registered.Error!;
        }
        var resourceId = registered.Value!.ResourceId;
        db.Tables.Add(new TableRow
        {
            ResourceId = resourceId, OwnerId = actor.UserId, SourceFormat = table.SourceFormat, ColumnCount = table.Columns.Count,
            RowCount = table.Rows.Count, DocumentBytes = bytes, Document = document, CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await AbortAsync(resourceId);
            if (admitted && actor.Refund is { } refund) await refund();
            throw;
        }

        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Table, actor.UserId, actor.UserId is null ? actor.GuestCreatorKey : null, expiry.Value,
            command.CustomSlug, title.Value, command.Password, RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess)
        {
            await AbortAsync(resourceId);
            if (admitted && actor.Refund is { } refund) await refund();
            return issued.Error!;
        }
        var link = issued.Value!;
        var bound = await resources.BindLinkAsync(resourceId, link.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;
        var ready = await resources.MarkReadyAsync(resourceId, bytes, ct);
        if (!ready.IsSuccess) return ready.Error!;
        return new ToolLinkCreatedDto(resourceId, link.Code, link.ShortUrl, link.LinkKey, link.ExpiresAt);
    }

    private async Task AbortAsync(Guid resourceId)
    {
        try
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            await db.Tables.Where(t => t.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up table resource {ResourceId} after a failed create", resourceId);
        }
    }

    // ---- Document --------------------------------------------------------------------------------------------------------------------

    private static string Serialize(ParsedTable table)
    {
        var root = new JsonObject
        {
            ["columns"] = new JsonArray(table.Columns.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["type"] = c.Type }).ToArray()),
            ["rows"] = new JsonArray(table.Rows.Select(r => (JsonNode)new JsonArray(r.Select(c => c?.DeepClone()).ToArray())).ToArray()),
        };
        return root.ToJsonString();
    }

    private static (List<TableColumn> Columns, List<JsonNode?[]> Rows) Deserialize(string document)
    {
        var root = JsonNode.Parse(document)!.AsObject();
        var columns = root["columns"]!.AsArray().Select(c => new TableColumn(c!["name"]!.GetValue<string>(), c["type"]!.GetValue<string>())).ToList();
        var rows = root["rows"]!.AsArray().Select(r => r!.AsArray().Select(c => c?.DeepClone()).ToArray()).ToList();
        return (columns, rows);
    }

    // ---- Reads ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Public descriptor (the caller passed the gate for this code), one page of rows.</summary>
    public async Task<Result<TableDescriptorDto>> DescribeAsync(Guid resourceId, LinkSnapshot link, string? title, int? offset, int? limit, CancellationToken ct)
    {
        var row = await db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var page = Page(row, offset, limit);
        if (!page.IsSuccess) return page.Error!;
        var (columns, rows, from, take) = page.Value;
        return new TableDescriptorDto("table", title, link.ExpiresAt ?? default, link.HasPassword, row.SourceFormat, row.RowCount,
            columns.Select(c => new TableColumnDto(c.Name, c.Type)).ToList(), from, take, Rows(rows));
    }

    public async Task<Result<OwnerTableDto>> GetOwnedAsync(Guid resourceId, Guid userId, string baseUrl, int? offset, int? limit, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Table, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var row = await db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var page = Page(row, offset, limit);
        if (!page.IsSuccess) return page.Error!;
        var (columns, rows, from, take) = page.Value;
        var o = owned.Value!;
        return new OwnerTableDto(resourceId, o.Link.Slug, $"{baseUrl.TrimEnd('/')}/{o.Link.Slug}", o.Resource.Title, o.State,
            o.Link.ExpiresAt ?? o.Resource.ExpiresAt, o.Link.LinkKeyHash is null, o.Link.HasPassword, row.SourceFormat, row.RowCount,
            columns.Select(c => new TableColumnDto(c.Name, c.Type)).ToList(), from, take, Rows(rows), row.DocumentBytes,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    private static Result<(List<TableColumn>, List<JsonNode?[]>, int, int)> Page(TableRow row, int? offset, int? limit)
    {
        var from = offset ?? 0;
        var take = limit ?? DefaultPageRows;
        if (from < 0) return Error.Validation("offset must be 0 or more.");
        if (take is < 1 or > MaxPageRows) return Error.Validation($"limit must be between 1 and {MaxPageRows}.");
        var (columns, rows) = Deserialize(row.Document);
        var slice = rows.Skip(from).Take(take).ToList();
        return (columns, slice, from, slice.Count);
    }

    private static JsonElement Rows(List<JsonNode?[]> rows)
    {
        var array = new JsonArray(rows.Select(r => (JsonNode)new JsonArray(r.ToArray())).ToArray());
        using var doc = JsonDocument.Parse(array.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>The whole table as CSV (formula-neutralised) or JSON (array of objects), returned as data for the browser to save.</summary>
    public async Task<Result<TableExportDto>> ExportAsync(Guid resourceId, string? title, string? format, CancellationToken ct)
    {
        var row = await db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var (columns, rows) = Deserialize(row.Document);
        var name = FileStem(title);
        return (format ?? "csv").ToLowerInvariant() switch
        {
            "csv" => new TableExportDto($"{name}.csv", "text/csv", TableExport.Csv(columns, rows)),
            "json" => new TableExportDto($"{name}.json", "application/json", TableExport.Json(columns, rows)),
            _ => Error.Validation("format must be csv or json."),
        };
    }

    public async Task<Result<TableExportDto>> ExportOwnedAsync(Guid resourceId, Guid userId, string? format, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Table, ct);
        if (!owned.IsSuccess) return owned.Error!;
        return await ExportAsync(resourceId, owned.Value!.Resource.Title, format, ct);
    }

    /// <summary>A safe file name stem from the title: letters, digits, dash and underscore only (default <c>table</c>).</summary>
    public static string FileStem(string? title)
    {
        var stem = new string((title ?? "").Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : c is ' ' or '-' or '_' ? '-' : '\0')
            .Where(c => c != '\0').ToArray()).Trim('-');
        return stem.Length == 0 ? "table" : stem[..Math.Min(stem.Length, 60)];
    }

    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Table, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State == "deleted") return true;
        if (owned.Value.State == "taken-down") return Error.Of(ErrorCodes.TakenDown, "This table was removed under the acceptable use policy.");
        var deleted = await linkCommands.DeleteAsync(owned.Value.Link.Id, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }
}

/// <summary>C2 kind handler: Resources reclaims a table (purge, guest expiry, orphan) and its row goes.</summary>
public sealed class TableKindHandler(CollabDbContext db) : IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Table;

    public Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        db.Tables.Where(t => t.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
