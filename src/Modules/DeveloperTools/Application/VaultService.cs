using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.DeveloperTools.Contracts;
using Zaagl.Modules.DeveloperTools.Domain;
using Zaagl.Modules.DeveloperTools.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.DeveloperTools.Application;

/// <summary>An owned secret with its authoritative state (link snapshot first, then the secret row).</summary>
public sealed record OwnedSecret(ResourceDescriptor Resource, SecretRow Secret, LinkSnapshot? Link, string State);

/// <summary>
/// E2.1 vault (docs/phase2/vault.md). Stores ONLY the client's ciphertext envelope and non-secret metadata; the decryption key
/// lives in the URL fragment and never reaches this class. Create order (as Phase 1 snippets, addendum §2.2): validate, plan,
/// guest counters, register (quota), secret row, link (C1, always keyed), bind, ready. A reveal is one row-locked transaction, so
/// exactly <c>max_views</c> reveals ever succeed. Nothing here logs or returns the ciphertext except <see cref="RevealAsync"/>.
/// </summary>
public sealed class VaultService(
    DevToolsDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    ILinkSnapshots snapshots,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    TimeProvider time,
    ILogger<VaultService> logger)
{
    public const int MaxLabelLength = 100;

    // ---------------------------------------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<SecretCreatedDto>> CreateAsync(ToolActor actor, CreateSecretCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = actor.IsGuest ? plans.Guest : await plans.GetForUserAsync(actor.UserId!.Value, ct);
        var tools = plan.ToolsOrNone;
        if (!tools.VaultEnabled)
            return actor.IsGuest ? Error.Unauthorized("Sign in to create a secret.") : Error.PlanRequired("The secret vault is not available on your plan.");

        // 1. Validate (no I/O). The envelope is checked structurally only (never decrypted).
        var envelope = VaultEnvelope.Parse(command.Ciphertext, command.PassphraseProtected, tools.VaultMaxCiphertextBytes, out var tooLarge);
        if (tooLarge) return Error.Of(ErrorCodes.SecretTooLarge, $"A secret can be at most {tools.VaultMaxCiphertextBytes} bytes of ciphertext on your plan.");
        if (!envelope.IsSuccess) return envelope.Error!;
        var bytes = envelope.Value!;

        var maxViews = command.MaxViews ?? 1;
        if (maxViews < 1 || maxViews > 100) return Error.Validation("maxViews must be between 1 and 100.");
        if (maxViews > tools.VaultMaxViews)
            return Error.PlanRequired($"Your plan allows at most {tools.VaultMaxViews} view(s) per secret.");

        string? label = null;
        if (!string.IsNullOrWhiteSpace(command.Label))
        {
            label = command.Label.Trim();
            if (label.Length > MaxLabelLength) return Error.Validation($"The label is at most {MaxLabelLength} characters.");
            if (label.Any(char.IsControl)) return Error.Validation("The label cannot contain control characters.");
        }
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");

        var maxExpiry = now.AddDays(plan.Limits.RetentionDays);
        DateTime expiresAt;
        if (command.ExpiresAt is { } requested)
        {
            expiresAt = requested.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(requested, DateTimeKind.Utc) : requested.ToUniversalTime();
            if (expiresAt <= now.AddMinutes(1)) return Error.Validation("The expiry must be at least a minute in the future.");
            if (expiresAt > maxExpiry) return Error.Of(ErrorCodes.ExpiryBeyondPlan, $"Your plan allows at most {plan.Limits.RetentionDays} days.");
        }
        else
        {
            expiresAt = maxExpiry;
        }

        // 2. Guest counters (PD-14, shared with snippets and uploads): counted after the plan checks.
        if (actor.TryAdmit is { } admit && !await admit(ct))
            return Error.Of(ErrorCodes.Quota, "Guest limit reached (30 per hour, 60 per day). Sign in or try again later.");

        var created = await CreateCoreAsync(actor, bytes, command.PassphraseProtected, (short)maxViews, label, command.Password,
            command.CustomSlug, expiresAt, ct);
        if (!created.IsSuccess && actor.Refund is { } refund) await refund();
        return created;
    }

    private async Task<Result<SecretCreatedDto>> CreateCoreAsync(
        ToolActor actor, byte[] bytes, bool passphrase, short maxViews, string? label, string? password, string? customSlug,
        DateTime expiresAt, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Secret, actor.Owner, bytes.Length, expiresAt, label, BurnAfterDownload: false, actor.Via), ct);
        if (!registered.IsSuccess) return registered.Error!;
        var resourceId = registered.Value!.ResourceId;

        db.Secrets.Add(new SecretRow
        {
            ResourceId = resourceId, OwnerId = actor.UserId, Ciphertext = bytes, CiphertextBytes = bytes.Length,
            EnvelopeVersion = bytes[0], PassphraseProtected = passphrase, MaxViews = maxViews, ViewsUsed = 0,
            ExpiresAt = expiresAt, CreatedAt = now,
        });
        db.SecretEvents.Add(new SecretEventRow { ResourceId = resourceId, Kind = SecretEventKinds.Created, ViewsUsed = 0, OccurredAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await AbortAsync(resourceId);
            throw;
        }

        // The link is always keyed: the link key stops anyone holding only the 30-bit code from burning the secret.
        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Secret, actor.UserId, actor.GuestCreatorKey, expiresAt,
            customSlug, label, password, RequireLinkKey: true), ct);
        if (!issued.IsSuccess)
        {
            await AbortAsync(resourceId);
            return issued.Error!;
        }
        var link = issued.Value!;
        var bound = await resources.BindLinkAsync(resourceId, link.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;
        var ready = await resources.MarkReadyAsync(resourceId, bytes.Length, ct);
        if (!ready.IsSuccess) return ready.Error!;

        return new SecretCreatedDto(resourceId, link.Code, link.ShortUrl, link.LinkKey, maxViews, link.ExpiresAt, actor.IsGuest);
    }

    /// <summary>Compensation after a failure past the register step: the resource never gets a link (unreachable), its reservation
    /// is released, and the ciphertext is removed at once.</summary>
    private async Task AbortAsync(Guid resourceId)
    {
        try
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            await db.SecretEvents.Where(e => e.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
            await db.Secrets.Where(s => s.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up vault resource {ResourceId} after a failed create", resourceId);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Public side (the caller has passed IAccessGate for the code)
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The descriptor. Consumes nothing. <paramref name="linkState"/> is the state the gate computed.</summary>
    public async Task<Result<SecretDescriptorDto>> DescribeAsync(Guid resourceId, LinkState? linkState, DateTime linkExpiresAt, bool hasPassword, CancellationToken ct)
    {
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, ct);
        if (secret is null) return Error.NotFound();
        if (PublicStateError(secret, linkState) is { } closed) return closed;
        return new SecretDescriptorDto("secret", "available", secret.MaxViews, secret.MaxViews - secret.ViewsUsed, linkExpiresAt,
            secret.PassphraseProtected, hasPassword, secret.CiphertextBytes, secret.EnvelopeVersion);
    }

    /// <summary>
    /// Consumes one view and returns the envelope. One transaction holding the row lock (<c>lock_timeout 3s</c>): the guard
    /// (ciphertext present, views left, not expired) is re-read under the lock, so under any concurrency exactly <c>max_views</c>
    /// reveals succeed and every other request is <c>410 burned</c>. The last view wipes the ciphertext in the same statement.
    /// </summary>
    public async Task<Result<SecretRevealDto>> RevealAsync(Guid resourceId, LinkState? linkState, CancellationToken ct)
    {
        if (linkState is not LinkState.Active) return Error.Of(ErrorCodes.Expired, "This secret has expired.");
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            var row = (await db.Secrets
                .FromSqlInterpolated($"SELECT * FROM devtools.secrets WHERE resource_id = {resourceId} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct)).FirstOrDefault();
            if (row is null) { await tx.RollbackAsync(CancellationToken.None); return Error.NotFound(); }
            if (row.Ciphertext is not { } envelope || row.ViewsUsed >= row.MaxViews)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return row.WipeReason is WipeReasons.Expired
                    ? Error.Of(ErrorCodes.Expired, "This secret has expired.")
                    : Error.Of(ErrorCodes.Burned, "This secret was already viewed and destroyed.");
            }
            if (row.ExpiresAt <= now)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return Error.Of(ErrorCodes.Expired, "This secret has expired.");
            }

            var used = (short)(row.ViewsUsed + 1);
            var last = used >= row.MaxViews;
            var updated = last
                ? await db.Secrets.Where(s => s.ResourceId == resourceId && s.ViewsUsed == row.ViewsUsed && s.Ciphertext != null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ViewsUsed, used)
                        .SetProperty(x => x.LastViewedAt, now)
                        .SetProperty(x => x.Ciphertext, (byte[]?)null)
                        .SetProperty(x => x.WipedAt, now)
                        .SetProperty(x => x.WipeReason, WipeReasons.Burned), ct)
                : await db.Secrets.Where(s => s.ResourceId == resourceId && s.ViewsUsed == row.ViewsUsed && s.Ciphertext != null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ViewsUsed, used)
                        .SetProperty(x => x.LastViewedAt, now), ct);
            if (updated != 1)
            {
                // Cannot happen under the row lock; refuse rather than hand out a view that was not counted.
                await tx.RollbackAsync(CancellationToken.None);
                return Error.Of(ErrorCodes.Burned, "This secret was already viewed and destroyed.");
            }
            db.SecretEvents.Add(new SecretEventRow { ResourceId = resourceId, Kind = SecretEventKinds.Revealed, ViewsUsed = used, OccurredAt = now });
            if (last) db.SecretEvents.Add(new SecretEventRow { ResourceId = resourceId, Kind = SecretEventKinds.Burned, ViewsUsed = used, OccurredAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SecretRevealDto(Base64Url.EncodeToString(envelope), row.MaxViews - used, last, row.EnvelopeVersion);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Error.Of(ErrorCodes.Busy, "This secret is busy. Try again in a second.");
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static Error? PublicStateError(SecretRow secret, LinkState? linkState)
    {
        if (linkState is not LinkState.Active) return Error.Of(ErrorCodes.Expired, "This secret has expired.");
        if (secret.Ciphertext is null || secret.ViewsUsed >= secret.MaxViews)
        {
            return secret.WipeReason switch
            {
                WipeReasons.Expired => Error.Of(ErrorCodes.Expired, "This secret has expired."),
                WipeReasons.Deleted => Error.Of(ErrorCodes.Burned, "This secret was destroyed by its owner."),
                WipeReasons.Disabled => Error.Of(ErrorCodes.Disabled, "This secret has been disabled."),
                _ => Error.Of(ErrorCodes.Burned, "This secret was already viewed and destroyed."),
            };
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------
    // Owner side (PD-40: owner only, 404 for everyone else)
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<OwnedSecret>> ResolveOwnedAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Kind != ResourceKind.Secret || resource.Owner.UserId != userId) return Error.NotFound();
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, ct);
        if (secret is null) return Error.NotFound();
        var link = resource.LinkId is { } linkId ? await snapshots.GetSnapshotByIdAsync(linkId, ct) : null;
        if (link is null) return Error.NotFound();
        var state = OwnerState(link, secret, time.GetUtcNow().UtcDateTime);
        return state is null ? Error.NotFound() : Result<OwnedSecret>.Ok(new OwnedSecret(resource, secret, link, state));
    }

    /// <summary>Null = purgeable (not visible to the owner).</summary>
    internal static string? OwnerState(LinkSnapshot link, SecretRow secret, DateTime now)
    {
        var linkState = LinkStateCalculator.Compute(link, now);
        if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        return linkState switch
        {
            LinkState.Purgeable => null,
            LinkState.Deleted => "deleted",
            LinkState.Disabled => link.DisabledKind is "abuse" or "safety" ? "taken-down" : "disabled",
            _ when secret.WipeReason == WipeReasons.Burned || (secret.Ciphertext is not null && secret.ViewsUsed >= secret.MaxViews) => "burned",
            LinkState.Quarantined => "expired",
            _ when secret.Ciphertext is null => secret.WipeReason ?? "burned",
            _ => "available",
        };
    }

    public async Task<IReadOnlyList<OwnerSecretDto>> ListAsync(Guid userId, string baseUrl, CancellationToken ct)
    {
        var rows = await db.Secrets.AsNoTracking().Where(s => s.OwnerId == userId)
            .OrderByDescending(s => s.CreatedAt).Take(100).ToListAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var result = new List<OwnerSecretDto>(rows.Count);
        foreach (var row in rows)
        {
            var resource = await resources.GetAsync(row.ResourceId, ct);
            if (resource?.LinkId is not { } linkId || resource.Owner.UserId != userId) continue;
            var link = await snapshots.GetSnapshotByIdAsync(linkId, ct);
            if (link is null || OwnerState(link, row, now) is not { } state) continue;
            result.Add(ToOwnerDto(resource, row, link, state, baseUrl, null));
        }
        return result;
    }

    public async Task<IReadOnlyList<SecretEventDto>> EventsAsync(Guid resourceId, CancellationToken ct) =>
        (await db.SecretEvents.AsNoTracking().Where(e => e.ResourceId == resourceId).OrderBy(e => e.Id).Take(500).ToListAsync(ct))
        .Select(e => new SecretEventDto(e.Kind, e.ViewsUsed, DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc))).ToList();

    public static OwnerSecretDto ToOwnerDto(ResourceDescriptor r, SecretRow s, LinkSnapshot link, string state, string baseUrl, IReadOnlyList<SecretEventDto>? events) =>
        new(r.ResourceId, r.Title, link.Slug, $"{baseUrl.TrimEnd('/')}/{link.Slug}", state, s.MaxViews, s.ViewsUsed, s.CiphertextBytes,
            s.PassphraseProtected, link.HasPassword, DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc),
            link.ExpiresAt ?? s.ExpiresAt, Utc(s.LastViewedAt), Utc(s.WipedAt), s.WipeReason, events);

    /// <summary>Owner "destroy now": the link becomes deleted (410) and the ciphertext is wiped in this request (it does not wait
    /// for the C10 event). Destroying an already destroyed secret succeeds.</summary>
    public async Task<Result<bool>> DestroyAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ResolveOwnedAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State == "taken-down") return Error.Of(ErrorCodes.TakenDown, "This secret was removed under the acceptable use policy.");
        await WipeAsync(resourceId, WipeReasons.Deleted, ct);
        if (owned.Value.State == "deleted" || owned.Value.Resource.LinkId is not { } linkId) return true;
        var deleted = await linkCommands.DeleteAsync(linkId, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }

    /// <summary>Idempotent wipe: clears the ciphertext once and records one audit event. Returns true when this call wiped it.</summary>
    public async Task<bool> WipeAsync(Guid resourceId, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var wiped = await db.Secrets.Where(s => s.ResourceId == resourceId && s.Ciphertext != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Ciphertext, (byte[]?)null)
                .SetProperty(x => x.WipedAt, now)
                .SetProperty(x => x.WipeReason, reason), ct);
        if (wiped == 0) return false;
        var views = await db.Secrets.AsNoTracking().Where(s => s.ResourceId == resourceId).Select(s => s.ViewsUsed).FirstOrDefaultAsync(ct);
        db.SecretEvents.Add(new SecretEventRow { ResourceId = resourceId, Kind = reason, ViewsUsed = views, OccurredAt = now });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
