using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Collaboration.Domain;
using Zaagl.Modules.Collaboration.Infrastructure;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Collaboration.Application;

/// <summary>
/// The pointer timeline of a voice-over brief, version 1 (docs/phase2/voice-briefs.md). Strict: unknown fields, wrong types, NUL, a time
/// outside 0..duration or going backwards, coordinates outside 0..1, a page on an image subject, are all 400. The stored document is
/// rebuilt from the known fields only.
/// </summary>
public static class BriefTimeline
{
    public const int MaxNoteChars = 140;
    public const int MaxPage = 10_000;
    /// <summary>Raw bytes allowed per event (a generous upper bound for <c>{"t":…,"k":"note","x":…,"y":…,"p":…,"text":"…"}</c>).</summary>
    public const int BytesPerEvent = 400;

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { "point", "tap", "note", "hide", "page" };

    public static Result<(string Json, int Events)> Normalize(JsonElement timeline, int durationMs, bool document, int maxEvents)
    {
        if (timeline.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return (Serialize([]), 0);
        if (timeline.ValueKind != JsonValueKind.Object) return Error.Validation("timeline must be an object {\"v\":1,\"events\":[…]}.");
        if (Encoding.UTF8.GetByteCount(timeline.GetRawText()) > Math.Max(1, maxEvents) * BytesPerEvent + 64)
            return Error.Of(ErrorCodes.InputTooLarge, $"The timeline can have at most {maxEvents} events on your plan.");
        int? version = null;
        JsonElement? events = null;
        foreach (var p in timeline.EnumerateObject())
        {
            switch (p.Name)
            {
                case "v": if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v)) version = v; else return Bad("v must be 1."); break;
                case "events": events = p.Value; break;
                default: return Bad($"Unknown timeline field '{Trim(p.Name)}'.");
            }
        }
        if (version != 1) return Bad("timeline.v must be 1.");
        if (events is not { ValueKind: JsonValueKind.Array } list) return Bad("timeline.events must be an array.");
        if (list.GetArrayLength() > maxEvents) return Error.Of(ErrorCodes.InputTooLarge, $"The timeline can have at most {maxEvents} events on your plan.");

        var output = new List<JsonObject>();
        var last = 0;
        var index = 0;
        foreach (var e in list.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object) return Bad($"Event {index} must be an object.");
            int? t = null; string? k = null; double? x = null, y = null; int? page = null; string? text = null;
            foreach (var p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "t":
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var tv)) return Bad($"Event {index}: t must be an integer (ms).");
                        t = tv; break;
                    case "k":
                        if (p.Value.ValueKind != JsonValueKind.String) return Bad($"Event {index}: k must be a string.");
                        k = p.Value.GetString(); break;
                    case "x":
                        if (p.Value.ValueKind != JsonValueKind.Number) return Bad($"Event {index}: x must be a number.");
                        x = p.Value.GetDouble(); break;
                    case "y":
                        if (p.Value.ValueKind != JsonValueKind.Number) return Bad($"Event {index}: y must be a number.");
                        y = p.Value.GetDouble(); break;
                    case "p":
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var pv)) return Bad($"Event {index}: p must be an integer page.");
                        page = pv; break;
                    case "text":
                        if (p.Value.ValueKind != JsonValueKind.String) return Bad($"Event {index}: text must be a string.");
                        text = p.Value.GetString(); break;
                    default: return Bad($"Event {index}: unknown field '{Trim(p.Name)}'.");
                }
            }
            if (t is not { } time || time < 0 || time > durationMs) return Bad($"Event {index}: t must be between 0 and the duration ({durationMs} ms).");
            if (time < last) return Bad($"Event {index}: events must be in time order.");
            last = time;
            if (k is null || !Kinds.Contains(k)) return Bad($"Event {index}: k must be one of {string.Join(", ", Kinds)}.");
            var needsPoint = k is "point" or "tap" or "note";
            if (needsPoint && (x is not { } xv || y is not { } yv || !double.IsFinite(xv) || !double.IsFinite(yv) || xv is < 0 or > 1 || yv is < 0 or > 1))
                return Bad($"Event {index}: x and y must be between 0 and 1.");
            if (!needsPoint && (x is not null || y is not null)) return Bad($"Event {index}: '{k}' has no position.");
            if (k == "note")
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > MaxNoteChars || text.Any(c => char.IsControl(c) || c == '\0'))
                    return Bad($"Event {index}: a note's text is 1-{MaxNoteChars} characters without control characters.");
            }
            else if (text is not null) return Bad($"Event {index}: only a note has text.");
            if (page is not null && !document) return Bad($"Event {index}: an image has no pages.");
            if (page is { } pg && pg is < 1 or > MaxPage) return Bad($"Event {index}: p must be between 1 and {MaxPage}.");
            if (k == "page" && page is null) return Bad($"Event {index}: a page event needs p.");

            var o = new JsonObject { ["t"] = time, ["k"] = k };
            if (needsPoint) { o["x"] = Math.Round(x!.Value, 5); o["y"] = Math.Round(y!.Value, 5); }
            if (page is not null) o["p"] = page;
            if (text is not null) o["text"] = text.Trim();
            output.Add(o);
        }
        return (Serialize(output), output.Count);
    }

    private static string Serialize(List<JsonObject> events) =>
        new JsonObject { ["v"] = 1, ["events"] = new JsonArray(events.Select(e => (JsonNode)e).ToArray()) }.ToJsonString();

    private static string Trim(string s) => s.Length > 40 ? s[..40] : s;

    private static Error Bad(string message) => Error.Validation(message);
}

/// <summary>What a brief knows about one referenced part (subject or audio), recomputed on every read.</summary>
public sealed record BriefPart(Guid ResourceId, bool Usable, string? Mime, long? SizeBytes, DateTime? LinkExpiresAt);

/// <summary>
/// E2.3 voice-over micro-briefs (docs/phase2/voice-briefs.md): a content link of kind <c>brief</c> (viewer <c>app.zaa.gl/vb/&lt;code&gt;</c>)
/// that plays a short voice recording (an existing audio upload of the owner) over an image or a PDF of the owner, with an optional pointer
/// timeline (normalised x,y + time) so the listener sees "pointing while talking". The parts are never copied; their state is re-checked on
/// every read. Create order (addendum §2.2): plan, parts, timeline, register (timeline bytes reserved), row, link (C1), bind, ready.
/// </summary>
public sealed class BriefService(
    CollabDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    ILinkSnapshots snapshots,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    IStoredObjectSummaries objects,
    IDownloadGrants grants,
    ImageParts images,
    ToolOwnership ownership,
    TimeProvider time,
    ILogger<BriefService> logger)
{
    /// <summary>Audio bytes allowed per second of the plan's maximum duration (384 kbit/s: any voice codec fits).</summary>
    public const int AudioBytesPerSecond = 48_000;
    /// <summary>Container overhead allowed on top.</summary>
    public const int AudioSlackBytes = 256 * 1024;

    /// <summary>Recordings a browser produces: MediaRecorder writes WebM (Chromium, Firefox) or MP4 (Safari); uploads may also be MP3,
    /// WAV, OGG, AAC, M4A or FLAC (validated audio viewer types).</summary>
    public static readonly IReadOnlySet<string> AudioMimes = new HashSet<string>(StringComparer.Ordinal)
    {
        "audio/mpeg", "audio/wav", "audio/flac", "audio/ogg", "audio/aac", "audio/mp4", "video/webm", "video/mp4",
    };

    public async Task<Result<ToolLinkCreatedDto>> CreateAsync(Guid userId, CreatedVia via, CreateBriefCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = await plans.GetForUserAsync(userId, ct);
        var tools = plan.ToolsOrNone;
        if (!tools.BriefsEnabled || tools.BriefMaxSeconds < 1) return Error.PlanRequired("Voice-over briefs are not available on your plan.");

        var maxMs = tools.BriefMaxSeconds * 1000;
        if (command.DurationMs is not { } duration || duration < 1 || duration > maxMs)
            return Error.Validation($"durationMs must be between 1 and {maxMs} on your plan.");
        var title = ToolOwnership.Label(command.Title, 200, "title");
        if (!title.IsSuccess) return title.Error!;
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");

        if (command.SubjectResourceId == Guid.Empty || command.AudioResourceId == Guid.Empty)
            return Error.Validation("subjectResourceId and audioResourceId are required.");
        if (command.SubjectResourceId == command.AudioResourceId) return Error.Validation("The subject and the audio must be different resources.");
        var (subjectKind, subject) = await SubjectAsync(command.SubjectResourceId, userId, ct);
        if (!subject.Usable)
            return Error.Of(ErrorCodes.ResourceUnusable, "The subject cannot be used: it must be one of your images (PNG, JPEG, GIF, WebP) or PDFs, scanned, ready and not expired.");
        var audio = await AudioAsync(command.AudioResourceId, userId, ct);
        if (!audio.Usable)
            return Error.Of(ErrorCodes.ResourceUnusable, "The audio cannot be used: it must be one of your audio recordings (WebM, MP4/M4A, MP3, WAV, OGG, AAC, FLAC), scanned, ready and not expired.");
        if (audio.SizeBytes > (long)tools.BriefMaxSeconds * AudioBytesPerSecond + AudioSlackBytes)
            return Error.Of(ErrorCodes.ResourceUnusable, $"The recording is larger than a {tools.BriefMaxSeconds}-second voice note can be on your plan.");

        var timeline = BriefTimeline.Normalize(command.Timeline, duration, subjectKind == "document", tools.BriefMaxPointerEvents);
        if (!timeline.IsSuccess) return timeline.Error!;
        var (json, events) = timeline.Value;
        var bytes = Encoding.UTF8.GetByteCount(json);

        var expiry = ToolOwnership.Expiry(command.ExpiresAt, now, plan.Limits.RetentionDays, [subject.LinkExpiresAt, audio.LinkExpiresAt]);
        if (!expiry.IsSuccess) return expiry.Error!;

        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Brief, new ContentOwner(userId, null), bytes, expiry.Value, title.Value, BurnAfterDownload: false, via), ct);
        if (!registered.IsSuccess) return registered.Error!;
        var resourceId = registered.Value!.ResourceId;
        db.Briefs.Add(new BriefRow
        {
            ResourceId = resourceId, OwnerId = userId, SubjectResourceId = command.SubjectResourceId, SubjectKind = subjectKind,
            AudioResourceId = command.AudioResourceId, DurationMs = duration, Timeline = json, EventCount = events, CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await AbortAsync(resourceId);
            throw;
        }
        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Brief, userId, null, expiry.Value, command.CustomSlug, title.Value, command.Password,
            RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess)
        {
            await AbortAsync(resourceId);
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
            await db.Briefs.Where(b => b.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up brief resource {ResourceId} after a failed create", resourceId);
        }
    }

    // ---- Parts ---------------------------------------------------------------------------------------------------------------------

    /// <summary>An image (<see cref="ImageParts"/> rules) or a PDF file of the owner. Kind <c>image</c> or <c>document</c>.</summary>
    private async Task<(string Kind, BriefPart Part)> SubjectAsync(Guid resourceId, Guid ownerId, CancellationToken ct)
    {
        var image = await images.DescribeAsync(resourceId, ownerId, ct);
        if (image.Usable) return ("image", new BriefPart(resourceId, true, image.Mime, null, image.LinkExpiresAt));
        var doc = await OwnedFileAsync(resourceId, ownerId, mime => mime == "application/pdf", null, ct);
        return ("document", doc);
    }

    private Task<BriefPart> AudioAsync(Guid resourceId, Guid ownerId, CancellationToken ct) =>
        OwnedFileAsync(resourceId, ownerId, AudioMimes.Contains, viewer => viewer is "audio" or "video", ct);

    /// <summary>Usable = owned, file or media, its own link active, ready, not burn, scanned clean, detected type accepted (and the
    /// validated viewer, when asked).</summary>
    private async Task<BriefPart> OwnedFileAsync(Guid resourceId, Guid ownerId, Func<string, bool> mimeOk, Func<string?, bool>? viewerOk, CancellationToken ct)
    {
        var unusable = new BriefPart(resourceId, false, null, null, null);
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Kind is not (ResourceKind.File or ResourceKind.Media) || resource.Owner.UserId != ownerId || resource.BurnAfterDownload)
            return unusable;
        if (resource.LinkId is not { } linkId || await snapshots.GetSnapshotByIdAsync(linkId, ct) is not { } link) return unusable;
        var linkState = LinkStateCalculator.Compute(link, time.GetUtcNow().UtcDateTime);
        if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        if (ResourceStateCalculator.Compute(linkState, resource.Processing, resource.BurnClaimedAt, null) != ResourceState.Ready) return unusable;
        var summary = (await objects.GetAsync([resourceId], ct)).GetValueOrDefault(resourceId);
        if (summary is not { Scan: ScanVerdict.Clean, DetectedMime: { } mime } || !mimeOk(mime)) return unusable;
        if (viewerOk is not null && !viewerOk(summary.Viewer)) return unusable;
        return new BriefPart(resourceId, true, mime, summary.SizeBytes, link.ExpiresAt);
    }

    private async Task<BriefMediaDto> AudioDtoAsync(BriefRow row, bool mint, CancellationToken ct)
    {
        var audio = await AudioAsync(row.AudioResourceId, row.OwnerId, ct);
        if (!audio.Usable) return new BriefMediaDto("audio", "unavailable", null, null, null, null, null, null);
        if (!mint) return new BriefMediaDto("audio", "ready", audio.Mime, audio.SizeBytes, null, null, null, null);
        var ticket = await grants.MintAsync(new MintTicketRequest(row.AudioResourceId, DeliveryPurpose.Inline, null), ct);
        return ticket.IsSuccess
            ? new BriefMediaDto("audio", "ready", audio.Mime, audio.SizeBytes, null, null, ticket.Value!.Url, null)
            : new BriefMediaDto("audio", "unavailable", null, null, null, null, null, null);
    }

    /// <summary>The subject: an image gets an inline ticket (and a preview ticket on its thumbnail); a PDF gets an Inline ticket the viewer
    /// fetches and renders itself (PDF is served as an attachment with the sandbox CSP, never rendered by the browser on our origin).</summary>
    private async Task<BriefMediaDto> SubjectDtoAsync(BriefRow row, bool mint, CancellationToken ct)
    {
        if (row.SubjectKind == "image")
        {
            var image = await images.DescribeAsync(row.SubjectResourceId, row.OwnerId, ct);
            if (!mint) return image.Usable
                ? new BriefMediaDto("image", "ready", image.Mime, null, image.Width, image.Height, null, null)
                : new BriefMediaDto("image", "unavailable", null, null, null, null, null, null);
            var dto = await images.ToDtoAsync(image, null, ct);
            return new BriefMediaDto("image", dto.State, dto.Mime, null, dto.Width, dto.Height, dto.InlineUrl, dto.PreviewUrl);
        }
        var doc = await OwnedFileAsync(row.SubjectResourceId, row.OwnerId, mime => mime == "application/pdf", null, ct);
        if (!doc.Usable) return new BriefMediaDto("document", "unavailable", null, null, null, null, null, null);
        if (!mint) return new BriefMediaDto("document", "ready", doc.Mime, doc.SizeBytes, null, null, null, null);
        var ticket = await grants.MintAsync(new MintTicketRequest(row.SubjectResourceId, DeliveryPurpose.Inline, null), ct);
        return ticket.IsSuccess
            ? new BriefMediaDto("document", "ready", doc.Mime, doc.SizeBytes, null, null, ticket.Value!.Url, null)
            : new BriefMediaDto("document", "unavailable", null, null, null, null, null, null);
    }

    // ---- Reads ---------------------------------------------------------------------------------------------------------------------

    public async Task<Result<BriefDescriptorDto>> DescribeAsync(Guid resourceId, LinkSnapshot link, string? title, CancellationToken ct)
    {
        var row = await db.Briefs.AsNoTracking().FirstOrDefaultAsync(b => b.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var subject = await SubjectDtoAsync(row, true, ct);
        var audio = await AudioDtoAsync(row, true, ct);
        using var doc = JsonDocument.Parse(row.Timeline);
        return new BriefDescriptorDto("brief", subject.State == "ready" && audio.State == "ready" ? "ready" : "incomplete", title,
            link.ExpiresAt ?? default, link.HasPassword, row.DurationMs, subject, audio, doc.RootElement.Clone());
    }

    public async Task<Result<OwnerBriefDto>> GetOwnedAsync(Guid resourceId, Guid userId, string baseUrl, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Brief, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var row = await db.Briefs.AsNoTracking().FirstOrDefaultAsync(b => b.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var o = owned.Value!;
        return new OwnerBriefDto(resourceId, o.Link.Slug, $"{baseUrl.TrimEnd('/')}/{o.Link.Slug}", o.Resource.Title, o.State,
            o.Link.ExpiresAt ?? o.Resource.ExpiresAt, o.Link.LinkKeyHash is null, o.Link.HasPassword, row.SubjectResourceId, row.AudioResourceId,
            row.DurationMs, row.EventCount, await SubjectDtoAsync(row, false, ct), await AudioDtoAsync(row, false, ct),
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Brief, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State == "deleted") return true;
        if (owned.Value.State == "taken-down") return Error.Of(ErrorCodes.TakenDown, "This brief was removed under the acceptable use policy.");
        var deleted = await linkCommands.DeleteAsync(owned.Value.Link.Id, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }
}

/// <summary>C2 kind handler: Resources reclaims a brief and its row goes. The subject and the audio are separate resources.</summary>
public sealed class BriefKindHandler(CollabDbContext db) : IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Brief;

    public Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        db.Briefs.Where(b => b.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
