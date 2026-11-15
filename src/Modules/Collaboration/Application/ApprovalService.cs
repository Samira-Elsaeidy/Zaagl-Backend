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

public enum ApprovalState : short
{
    Draft = 0,
    Approved = 1,
    Paid = 2,
    Released = 3,
}

public enum ApprovalEvent
{
    /// <summary>The link holder clicks "Approve".</summary>
    Approve,
    /// <summary>A payment provider confirmed payment (not built: the seam for a later provider).</summary>
    Pay,
    /// <summary>The owner (or the automatic release-on-approval) releases the original.</summary>
    Release,
}

/// <summary>
/// docs/phase2/approval-gate.md: <c>draft → approved → released</c> and, for a later payment provider, <c>draft → paid → released</c>
/// (also <c>approved → paid</c>, so "approve, then pay" works). Pure and total: every other transition is refused. Nothing serves the
/// original before <see cref="ApprovalState.Released"/>.
/// </summary>
public static class ApprovalStateMachine
{
    public static ApprovalState? Next(ApprovalState current, ApprovalEvent e) => (current, e) switch
    {
        (ApprovalState.Draft, ApprovalEvent.Approve) => ApprovalState.Approved,
        (ApprovalState.Draft, ApprovalEvent.Pay) => ApprovalState.Paid,
        (ApprovalState.Approved, ApprovalEvent.Pay) => ApprovalState.Paid,
        (ApprovalState.Approved, ApprovalEvent.Release) => ApprovalState.Released,
        (ApprovalState.Paid, ApprovalEvent.Release) => ApprovalState.Released,
        _ => null,
    };

    public static string Name(ApprovalState s) => s.ToString().ToLowerInvariant();
}

/// <summary>
/// E2.2 watermark and client approval gate (docs/phase2/approval-gate.md). A content link of kind <c>approval</c> whose row points at one
/// of the owner's ready raster images. Until release, link holders get only the watermarked derivative (rendered by the worker from a
/// downscaled decode); the original is never ticketed. Every transition is one conditional UPDATE on the expected state, so concurrent
/// approvals cannot double-apply and a release can never happen from a draft.
/// </summary>
public sealed class ApprovalService(
    CollabDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    IWatermarkDerivatives watermarks,
    IMediaMetadata media,
    IDownloadGrants grants,
    ImageParts parts,
    ToolOwnership ownership,
    TimeProvider time,
    ILogger<ApprovalService> logger)
{
    public const string DefaultWatermark = "DRAFT";

    public async Task<Result<ToolLinkCreatedDto>> CreateAsync(Guid userId, CreatedVia via, CreateApprovalCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = await plans.GetForUserAsync(userId, ct);
        if (!plan.ToolsOrNone.ApprovalEnabled) return Error.PlanRequired("Watermarked approval links are available on the Starter plan.");

        var text = ToolOwnership.Label(command.WatermarkText, 60, "watermarkText");
        if (!text.IsSuccess) return text.Error!;
        var watermark = text.Value ?? DefaultWatermark;
        if (!watermarks.CanRender(watermark))
            return Error.Validation("The watermark text must contain letters or digits the watermark font can draw (Latin or Arabic script; emoji are not drawn).");
        var title = ToolOwnership.Label(command.Title, 200, "title");
        if (!title.IsSuccess) return title.Error!;
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");

        var draft = await parts.RequireAsync(command.DraftResourceId, userId, "draft", ct);
        if (!draft.IsSuccess) return draft.Error!;
        var expiry = ToolOwnership.Expiry(command.ExpiresAt, now, plan.Limits.RetentionDays, [draft.Value!.LinkExpiresAt]);
        if (!expiry.IsSuccess) return expiry.Error!;

        // The render is queued first (idempotent per image and text): by the time the link is shared it is usually ready.
        var kind = await watermarks.RequestAsync(command.DraftResourceId, watermark, ct);

        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Approval, new ContentOwner(userId, null), 0, expiry.Value, title.Value, BurnAfterDownload: false, via), ct);
        if (!registered.IsSuccess) return registered.Error!;
        var resourceId = registered.Value!.ResourceId;
        db.Approvals.Add(new ApprovalRow
        {
            ResourceId = resourceId, OwnerId = userId, DraftResourceId = command.DraftResourceId, WatermarkText = watermark,
            WatermarkKind = kind, ReleaseOnApproval = command.ReleaseOnApproval, State = (short)ApprovalState.Draft, CreatedAt = now,
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
            resourceId, LinkTargetKind.Approval, userId, null, expiry.Value, command.CustomSlug, title.Value, command.Password,
            RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess)
        {
            await AbortAsync(resourceId);
            return issued.Error!;
        }
        var link = issued.Value!;
        var bound = await resources.BindLinkAsync(resourceId, link.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;
        var ready = await resources.MarkReadyAsync(resourceId, 0, ct);
        if (!ready.IsSuccess) return ready.Error!;
        return new ToolLinkCreatedDto(resourceId, link.Code, link.ShortUrl, link.LinkKey, link.ExpiresAt);
    }

    private async Task AbortAsync(Guid resourceId)
    {
        try
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            await db.Approvals.Where(a => a.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up approval resource {ResourceId} after a failed create", resourceId);
        }
    }

    // ---- Transitions (each one conditional UPDATE on the expected state) ----------------------------------------------------------

    /// <summary>The link holder approves. Approving twice answers the current record (idempotent), never a second transition.</summary>
    public async Task<Result<ApprovalInfoDto>> ApproveAsync(Guid resourceId, Guid? userId, ApproveCommand command, CancellationToken ct)
    {
        var name = ToolOwnership.Label(command.Name, 100, "name");
        if (!name.IsSuccess) return name.Error!;
        var note = (command.Note ?? "").Trim();
        if (note.Length > 1000 || note.Any(c => char.IsControl(c) && c != '\n')) return Error.Validation("The note is at most 1000 characters, without control characters.");
        string? email = null;
        if (!string.IsNullOrWhiteSpace(command.Email))
        {
            email = command.Email.Trim();
            var at = email.IndexOf('@');
            if (email.Length > 254 || at < 1 || at != email.LastIndexOf('@') || at == email.Length - 1 || email.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                return Error.Validation("The email address is not valid.");
        }

        var row = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var now = time.GetUtcNow().UtcDateTime;
        var target = row.ReleaseOnApproval ? ApprovalState.Released : ApprovalState.Approved;
        var updated = await db.Approvals
            .Where(a => a.ResourceId == resourceId && a.State == (short)ApprovalState.Draft)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, (short)target)
                .SetProperty(a => a.ApprovedAt, now)
                .SetProperty(a => a.ApproverName, name.Value)
                .SetProperty(a => a.ApproverEmail, email)
                .SetProperty(a => a.ApprovalNote, note.Length == 0 ? null : note)
                .SetProperty(a => a.ApproverUserId, userId)
                .SetProperty(a => a.ReleasedAt, target == ApprovalState.Released ? now : null), ct);
        var current = await db.Approvals.AsNoTracking().FirstAsync(a => a.ResourceId == resourceId, ct);
        if (updated == 0 && current.State == (short)ApprovalState.Draft) return Error.Conflict("The approval could not be recorded. Try again.");
        return Info(current);
    }

    /// <summary>Owner release (when <c>releaseOnApproval</c> is off): approved or paid → released. Anything else is 409.</summary>
    public async Task<Result<ApprovalInfoDto>> ReleaseAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Approval, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Approvals
            .Where(a => a.ResourceId == resourceId && (a.State == (short)ApprovalState.Approved || a.State == (short)ApprovalState.Paid))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, (short)ApprovalState.Released).SetProperty(a => a.ReleasedAt, now), ct);
        var current = await db.Approvals.AsNoTracking().FirstAsync(a => a.ResourceId == resourceId, ct);
        if (updated == 0 && current.State != (short)ApprovalState.Released)
            return Error.Conflict("Only an approved (or paid) draft can be released.");
        return Info(current);
    }

    /// <summary>
    /// The payment seam (not exposed: no provider is built). A provider's verified webhook would call this with its own reference; it
    /// moves draft or approved to paid (and on to released when <c>releaseOnApproval</c> is on). Idempotent per reference.
    /// </summary>
    public async Task<Result<ApprovalInfoDto>> MarkPaidAsync(Guid resourceId, string provider, string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 32 || string.IsNullOrWhiteSpace(reference) || reference.Length > 128)
            return Error.Validation("A payment needs a provider (<= 32) and a reference (<= 128).");
        var row = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        if (row.PaymentReference == reference) return Info(row);
        var now = time.GetUtcNow().UtcDateTime;
        var release = row.ReleaseOnApproval;
        var updated = await db.Approvals
            .Where(a => a.ResourceId == resourceId && (a.State == (short)ApprovalState.Draft || a.State == (short)ApprovalState.Approved))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, (short)(release ? ApprovalState.Released : ApprovalState.Paid))
                .SetProperty(a => a.PaidAt, now)
                .SetProperty(a => a.PaymentProvider, provider)
                .SetProperty(a => a.PaymentReference, reference)
                .SetProperty(a => a.ReleasedAt, a => release ? now : a.ReleasedAt), ct);
        var current = await db.Approvals.AsNoTracking().FirstAsync(a => a.ResourceId == resourceId, ct);
        if (updated == 0 && current.PaymentReference != reference) return Error.Conflict("This draft cannot take a payment in its current state.");
        return Info(current);
    }

    // ---- Reads ---------------------------------------------------------------------------------------------------------------------

    public Task<ApprovalRow?> GetRowAsync(Guid resourceId, CancellationToken ct) =>
        db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.ResourceId == resourceId, ct);

    public async Task<Result<ApprovalDescriptorDto>> DescribeAsync(Guid resourceId, LinkSnapshot link, string? title, CancellationToken ct)
    {
        var row = await GetRowAsync(resourceId, ct);
        if (row is null) return Error.NotFound();
        var draft = await parts.DescribeAsync(row.DraftResourceId, row.OwnerId, ct);
        var released = row.State == (short)ApprovalState.Released;
        var (previewState, preview) = await PreviewAsync(row, draft, ct);
        ImagePartDto? original = released ? await parts.ToDtoAsync(draft, null, ct) : null;
        return new ApprovalDescriptorDto("approval", title, link.ExpiresAt ?? default, link.HasPassword, Info(row), previewState,
            released ? null : preview, original, row.State == (short)ApprovalState.Draft && draft.Usable);
    }

    /// <summary>Released only: a Download ticket on the original (the button's POST, addendum §8.1.2). Never before release.</summary>
    public async Task<Result<DeliveryTicket>> DownloadAsync(Guid resourceId, CancellationToken ct)
    {
        var row = await GetRowAsync(resourceId, ct);
        if (row is null) return Error.NotFound();
        if (row.State != (short)ApprovalState.Released) return Error.Forbidden("The original is available once the draft is approved.");
        var draft = await parts.DescribeAsync(row.DraftResourceId, row.OwnerId, ct);
        if (!draft.Usable) return Error.Of(ErrorCodes.Gone, "The original is no longer available.");
        return await grants.MintAsync(new MintTicketRequest(row.DraftResourceId, DeliveryPurpose.Download, null), ct);
    }

    public async Task<Result<OwnerApprovalDto>> GetOwnedAsync(Guid resourceId, Guid userId, string baseUrl, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Approval, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var row = await GetRowAsync(resourceId, ct);
        if (row is null) return Error.NotFound();
        var o = owned.Value!;
        var draft = await parts.DescribeAsync(row.DraftResourceId, row.OwnerId, ct);
        var (previewState, _) = await PreviewAsync(row, draft, ct, mint: false);
        return new OwnerApprovalDto(resourceId, o.Link.Slug, $"{baseUrl.TrimEnd('/')}/{o.Link.Slug}", o.Resource.Title, o.State,
            o.Link.ExpiresAt ?? o.Resource.ExpiresAt, o.Link.LinkKeyHash is null, o.Link.HasPassword, row.DraftResourceId, row.WatermarkText,
            row.ReleaseOnApproval, Info(row), row.ApproverEmail, row.ApprovalNote, previewState, DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Approval, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State == "deleted") return true;
        if (owned.Value.State == "taken-down") return Error.Of(ErrorCodes.TakenDown, "This approval link was removed under the acceptable use policy.");
        var deleted = await linkCommands.DeleteAsync(owned.Value.Link.Id, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }

    /// <summary>The watermarked preview: <c>ready</c> with a Preview ticket on the <c>wm-…</c> derivative, <c>processing</c> while the
    /// worker renders it, <c>unavailable</c> when the draft image is gone or the render failed.</summary>
    private async Task<(string State, ImagePartDto? Preview)> PreviewAsync(ApprovalRow row, ImagePart draft, CancellationToken ct, bool mint = true)
    {
        if (!draft.Usable) return ("unavailable", null);
        var derivative = (await media.GetAsync([row.DraftResourceId], ct)).GetValueOrDefault(row.DraftResourceId)?
            .Derivatives.FirstOrDefault(d => d.Kind == row.WatermarkKind);
        if (derivative is null || derivative.Status == "pending") return ("processing", null);
        if (derivative.Status != "ready") return ("unavailable", null);
        if (!mint) return ("ready", null);
        var ticket = await grants.MintAsync(new MintTicketRequest(row.DraftResourceId, DeliveryPurpose.Preview, row.WatermarkKind), ct);
        return ticket.IsSuccess
            ? ("ready", new ImagePartDto(null, "ready", derivative.Mime, derivative.Width, derivative.Height, ticket.Value!.Url, null))
            : ("unavailable", null);
    }

    private static ApprovalInfoDto Info(ApprovalRow row) => new(
        ApprovalStateMachine.Name((ApprovalState)row.State), Utc(row.ApprovedAt), row.ApproverName, Utc(row.PaidAt), Utc(row.ReleasedAt));

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}

/// <summary>C2 kind handler: Resources reclaims an approval link and its row goes. The draft image is a separate resource.</summary>
public sealed class ApprovalKindHandler(CollabDbContext db) : IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Approval;

    public Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        db.Approvals.Where(a => a.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
