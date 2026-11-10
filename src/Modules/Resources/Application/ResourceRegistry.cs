using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Resources.Domain;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Resources.Application;

public sealed class ResourcesOptions
{
    /// <summary>Same value as the Links quarantine (spec §1.1, ShortLinks:QuarantineDays); used only to fill the
    /// <c>link_purge_after</c> copy at registration. The link row stays authoritative.</summary>
    public int QuarantineDays { get; set; } = 90;

    /// <summary>T8 (ARC-009 §7 step 6b): how long infected or blocked bytes stay quarantined under a legal hold.
    /// PD-11 is OPEN (counsel): this is configuration (<c>Abuse:EvidenceRetentionDays</c>), not a decided value.</summary>
    public int EvidenceRetentionDays { get; set; } = 30;
}

/// <summary>
/// C2 <see cref="IResourceRegistry"/> (ARC-006 §3). Every transition is a guarded conditional UPDATE: zero rows means
/// the transition already happened or is not allowed, so a replay changes nothing and throws nothing.
/// Plan checks (size, retention, burn, password) are the caller's, through C5, before RegisterAsync.
/// Not in Stage 1 (no consumer yet): byte moves (Files tasks), the C10 lifecycle handler (needs the Links outbox),
/// download grants (BE-018), the orphan sweeper (T19).
/// </summary>
public sealed class ResourceRegistry(
    ResourcesDbContext db,
    ContentQuota quota,
    IOptions<ResourcesOptions> options,
    TimeProvider time,
    ILinkTakedown? takedown = null) : IResourceRegistry
{
    public async Task<Result<ResourceRegistration>> RegisterAsync(RegisterResourceCommand command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = time.GetUtcNow().UtcDateTime;
        var owner = command.Owner;
        if ((owner.UserId is null) == (owner.GuestCreatorKey is null))
            return Error.Validation("Exactly one of owner and guest creator key must be set.");
        if (!Enum.IsDefined(command.Kind)) return Error.Validation("Unknown content kind.");
        if (command.DeclaredSizeBytes < 0) return Error.Validation("The size cannot be negative.");
        var expiresAt = DateTime.SpecifyKind(command.ExpiresAt, DateTimeKind.Utc);
        if (expiresAt <= now) return Error.Validation("Every content item needs an expiry in the future.");

        var row = new ResourceRow
        {
            Id = Guid.CreateVersion7(),
            PublicId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            Kind = command.Kind,
            OwnerId = owner.UserId,
            GuestCreatorKey = owner.GuestCreatorKey,
            Title = string.IsNullOrWhiteSpace(command.Title) ? null : command.Title.Trim()[..Math.Min(command.Title.Trim().Length, 200)],
            DeclaredSizeBytes = command.DeclaredSizeBytes,
            ProcessingStatus = ProcessingStatus.Pending,
            BurnAfterDownload = command.BurnAfterDownload,
            LinkExpiresAt = expiresAt,
            // PD-06: account content keeps its bytes through the quarantine; guest content is purged at expiry.
            LinkPurgeAfter = owner.IsGuest ? expiresAt : expiresAt.AddDays(options.Value.QuarantineDays),
            BytesState = BytesState.None,
            CreatedVia = command.CreatedVia,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // T1: the row and the quota reservation commit together.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // F-9: an upload already holds its reservation (taken at tus create); adopt it instead of reserving again.
        var reservation = command.AdoptReservationId is { } adopt
            ? (owner.IsGuest && adopt == Guid.Empty
                ? Result<Guid>.Ok(Guid.Empty)
                : await quota.AdoptReservationAsync(adopt, owner, command.DeclaredSizeBytes, row.Id, ct))
            : MapReservation(await quota.ReserveForResourceAsync(owner, command.DeclaredSizeBytes, row.Id, ct));
        if (!reservation.IsSuccess)
        {
            await tx.RollbackAsync(ct);
            return reservation.Error!;
        }
        row.ReservationId = reservation.Value == Guid.Empty ? null : reservation.Value;
        db.Resources.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Result<ResourceRegistration>.Ok(new(row.Id, row.PublicId));
    }

    private static Result<Guid> MapReservation(Result<QuotaReservation> reserved) =>
        reserved.IsSuccess ? Result<Guid>.Ok(reserved.Value!.ReservationId) : reserved.Error!;

    /// <summary>T2: set once; binding the same link again is a no-op, another link is refused.</summary>
    public async Task<Result<bool>> BindLinkAsync(Guid resourceId, Guid linkId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var rows = await db.Resources.Where(r => r.Id == resourceId && r.LinkId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LinkId, linkId).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (rows > 0) return Result<bool>.Ok(true);

        var current = await db.Resources.AsNoTracking().Where(r => r.Id == resourceId)
            .Select(r => new { r.LinkId }).FirstOrDefaultAsync(cancellationToken);
        if (current is null) return Error.NotFound("Resource not found.");
        return current.LinkId == linkId ? Result<bool>.Ok(false) : Error.Conflict("This resource already has a link.");
    }

    /// <summary>T4: Pending → Scanning.</summary>
    public Task<Result<bool>> MarkScanningAsync(Guid resourceId, CancellationToken cancellationToken) =>
        GuardedAsync(resourceId,
            q => q.Where(r => r.ProcessingStatus == ProcessingStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ProcessingStatus, ProcessingStatus.Scanning)
                    .SetProperty(r => r.UpdatedAt, time.GetUtcNow().UtcDateTime), cancellationToken),
            cancellationToken);

    /// <summary>T3 (snippet, from Pending) and T5 (scan clean, from Scanning or Error): Ready with the final size, and
    /// the reservation becomes used bytes. A final size above the reservation rejects the resource (too-large).</summary>
    public async Task<Result<bool>> MarkReadyAsync(Guid resourceId, long finalSizeBytes, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var row = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (row is null) return Error.NotFound("Resource not found.");
        if (row.ProcessingStatus is not (ProcessingStatus.Pending or ProcessingStatus.Scanning or ProcessingStatus.Error))
            return Result<bool>.Ok(false);
        if (finalSizeBytes < 0) return Error.Validation("The size cannot be negative.");

        if (finalSizeBytes > row.DeclaredSizeBytes)
        {
            await MarkRejectedAsync(resourceId, RejectedReasons.TooLarge, ct);
            return Error.Of(ErrorCodes.FileTooLarge, "The stored content is larger than declared.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var rows = await db.Resources
            .Where(r => r.Id == resourceId && (r.ProcessingStatus == ProcessingStatus.Pending
                || r.ProcessingStatus == ProcessingStatus.Scanning || r.ProcessingStatus == ProcessingStatus.Error))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ProcessingStatus, ProcessingStatus.Ready)
                .SetProperty(r => r.SizeBytes, finalSizeBytes)
                .SetProperty(r => r.BytesState, BytesState.Stored)
                .SetProperty(r => r.UpdatedAt, now), ct);
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return Result<bool>.Ok(false);
        }
        if (row.ReservationId is { } reservationId)
        {
            var committed = await quota.CommitAsync(reservationId, finalSizeBytes, ct);
            if (!committed.IsSuccess)
            {
                await tx.RollbackAsync(ct);
                return committed.Error!;
            }
        }
        await tx.CommitAsync(ct);
        return Result<bool>.Ok(true);
    }

    /// <summary>T7/T8/T9: Rejected with a reason; the reservation (or used bytes) is released. Moving bytes to
    /// quarantine and the takedown are Files/Links work (C12), triggered by the caller.</summary>
    public Task<Result<bool>> MarkRejectedAsync(Guid resourceId, string reason, CancellationToken cancellationToken) =>
        MarkRejectedAsync(resourceId, reason, null, cancellationToken);

    /// <summary>F-10, T8/T9: for <c>infected</c> and <c>blocked-hash</c> the bytes are marked quarantined with a legal hold
    /// (<see cref="ResourcesOptions.EvidenceRetentionDays"/>) and, when a takedown (C12) is registered, the link is taken
    /// down with the evidence. The takedown is best effort: the resource is already <c>rejected</c>, which no route serves.</summary>
    public async Task<Result<bool>> MarkRejectedAsync(
        Guid resourceId, string reason, RejectionEvidence? evidence, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (reason is not (RejectedReasons.Infected or RejectedReasons.TypeNotAllowed or RejectedReasons.TooLarge
            or RejectedReasons.BlockedHash or RejectedReasons.ScanUnavailable or RejectedReasons.CreateFailed))
            return Error.Validation("Unknown rejection reason.");

        var row = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (row is null) return Error.NotFound("Resource not found.");

        var now = time.GetUtcNow().UtcDateTime;
        var quarantine = reason is RejectedReasons.Infected or RejectedReasons.BlockedHash;
        var holdUntil = now.AddDays(Math.Max(0, options.Value.EvidenceRetentionDays));
        var rows = await db.Resources.Where(r => r.Id == resourceId && r.ProcessingStatus != ProcessingStatus.Rejected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ProcessingStatus, ProcessingStatus.Rejected)
                .SetProperty(r => r.RejectedReason, reason)
                .SetProperty(r => r.BytesState, r => quarantine ? BytesState.Quarantined : r.BytesState)
                .SetProperty(r => r.LegalHoldUntil, r => quarantine ? holdUntil : r.LegalHoldUntil)
                .SetProperty(r => r.UpdatedAt, now), ct);
        if (rows == 0) return Result<bool>.Ok(false);

        if (row.ReservationId is { } reservationId) await quota.ReleaseAsync(reservationId, ct);
        await quota.ReleaseUsedAsync(resourceId, ct); // T9 after Ready: used bytes are freed too (no-op otherwise)

        if (quarantine && takedown is not null && row.LinkId is { } linkId)
        {
            await takedown.TakeDownContentAsync(new ContentTakedownRequest(
                linkId, resourceId,
                reason == RejectedReasons.Infected ? ContentTakedownKind.Safety : ContentTakedownKind.Abuse,
                reason, evidence?.Sha256), ct);
        }
        return Result<bool>.Ok(true);
    }

    public async Task<ResourceDescriptor?> GetAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var row = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId, cancellationToken);
        return row is null ? null : await DescribeAsync(row, cancellationToken);
    }

    public async Task<ResourceDescriptor?> GetByLinkAsync(Guid linkId, CancellationToken cancellationToken)
    {
        var row = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.LinkId == linkId, cancellationToken);
        return row is null ? null : await DescribeAsync(row, cancellationToken);
    }

    /// <summary>
    /// The registry has no view of the link row, so the descriptor's state uses a provisional link state computed from
    /// the copied dates (<c>link_expires_at</c>, <c>link_purge_after</c>) and <c>deleted_at</c>; it cannot see a
    /// disabled link. Public and owner routes MUST recompute with <see cref="ResourceStateCalculator"/> from the
    /// authoritative link state (AccessGateResult.LinkState or the snapshot), ARC-006 §2.
    /// </summary>
    private async Task<ResourceDescriptor> DescribeAsync(ResourceRow row, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await Task.CompletedTask;
        return Describe(row, row.DownloadCount, now); // S-1: the durable column, never derived from grants
    }

    internal static ResourceDescriptor Describe(ResourceRow row, long downloads, DateTime now)
    {
        LinkState? provisional = row.LinkId is null
            ? null
            : LinkStateCalculator.Compute(row.LinkPurgeAfter, row.DeletedAt, null, row.LinkExpiresAt, now);
        return new ResourceDescriptor(
            row.Id, row.PublicId, row.Kind, new ContentOwner(row.OwnerId, row.GuestCreatorKey), row.LinkId, row.Title,
            row.SizeBytes ?? row.DeclaredSizeBytes,
            ResourceStateCalculator.Compute(provisional, row.ProcessingStatus, row.BurnClaimedAt, row.DeletedAt),
            row.RejectedReason, row.LinkExpiresAt, row.LinkPurgeAfter, row.BurnAfterDownload, row.BurnClaimedAt,
            downloads, row.CreatedAt, row.ProcessingStatus);
    }

    private async Task<Result<bool>> GuardedAsync(
        Guid resourceId, Func<IQueryable<ResourceRow>, Task<int>> update, CancellationToken ct)
    {
        var rows = await update(db.Resources.Where(r => r.Id == resourceId));
        if (rows > 0) return Result<bool>.Ok(true);
        return await db.Resources.AnyAsync(r => r.Id == resourceId, ct)
            ? Result<bool>.Ok(false)
            : Error.NotFound("Resource not found.");
    }
}
