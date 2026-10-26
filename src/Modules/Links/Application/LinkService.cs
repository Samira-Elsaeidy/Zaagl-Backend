using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Modules.Links.Application;

public sealed class LinkOptions
{
    public const string Section = "ShortLinks";

    /// <summary>Public origin of short links; its host is blocked as a destination to prevent redirect loops.</summary>
    public string BaseUrl { get; set; } = "https://zaa.gl";

    /// <summary>Spec §1.1: how long an owned link answers 410 (quarantined, renewable) after it expires,
    /// before it becomes purgeable. Spec default 90, minimum 30. Guest links get none (handled separately).</summary>
    public int QuarantineDays { get; set; } = 90;
}

public static class LinkCache
{
    public static string Key(string slugKey) => $"link:{slugKey}";
}

public sealed class LinkService(
    LinksDbContext db,
    IPlanProvider plans,
    CoordinatorLeaseClient leases,
    CodeGenerator codes,
    CreationGate creationGate,
    IUrlReputationChecker reputation,
    IDestinationBlocklist blocklist,
    HybridCache cache,
    ILinkCacheInvalidator invalidator,
    IValidator<LinkInput> validator,
    IOptions<LinkOptions> options,
    IOptions<GuestOptions> guestOptions,
    TimeProvider time)
{
    private const int MaxSlugAttempts = 5;

    private readonly IReadOnlyCollection<string> _ownHosts = OwnHosts(options.Value.BaseUrl);

    private sealed record Normalized(
        string Destination,
        string? Fallback,
        string? OgImage,
        List<RuleInput> Rules,
        string[] Tags,
        string TargetHost,
        bool SafetyChecked);

    // ---- Create -------------------------------------------------------------
    // Spec §7.2's four phases, same names used below: A (identify/parse, no rules), B (stateless
    // validation), C (one transaction, no external calls inside it), D (response).

    public async Task<Result<LinkDto>> CreateAsync(Guid userId, LinkInput input, CancellationToken ct, string? idempotencyKey = null)
    {
        // ---- Phase A: identify the request ------------------------------------
        var customSlugRaw = string.IsNullOrWhiteSpace(input.CustomSlug) ? null : input.CustomSlug.Trim();
        byte[]? requestHash = idempotencyKey is null ? null : ComputeRequestHash(input, customSlugRaw);
        var scope = $"u:{userId}";

        // Idempotency fast path (spec §7.2 step 4): a stored "done" response replays exactly as stored,
        // whatever the current plan/quota/clock/availability now says. Checked before any business rule.
        if (idempotencyKey is not null)
        {
            var existing = await db.IdempotencyKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.Scope == scope && k.Key == idempotencyKey, ct);
            if (existing is { Status: "done" })
            {
                return existing.RequestHash.AsSpan().SequenceEqual(requestHash)
                    ? Result<LinkDto>.Ok(System.Text.Json.JsonSerializer.Deserialize<LinkDto>(existing.ResponseBody!)!)
                    : Error.Validation("idempotency-mismatch: this key was already used with a different request.");
            }
        }

        // ---- Phase B: stateless validation and preparation --------------------
        // Spec §5.4: both generated AND alias creation are gated on a successful key-fingerprint match
        // with the coordinator. Everything else (redirects, reads, renewals, deletes) is unaffected.
        if (!creationGate.CanCreate)
            return Error.ServiceUnavailable($"Link creation is temporarily unavailable ({creationGate.State}). Try again shortly.");

        var limits = await plans.GetLimitsAsync(userId, ct);

        if (limits.MonthlyLinks is { } cap)
        {
            var now0 = DateTime.UtcNow;
            var monthStart = new DateTime(now0.Year, now0.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var used = await db.Links.CountAsync(l => l.OwnerId == userId && l.CreatedAt >= monthStart, ct);
            if (used >= cap)
                return Error.PlanRequired($"The Free plan allows {cap} links per month. Upgrade to Starter for unlimited links.");
        }

        var prepared = await PrepareAsync(userId, input, limits, isCreate: true, ct);
        if (prepared.Error is not null) return prepared.Error;
        var n = prepared.Value!;

        if (input.Policy?.ExpiresAt is { } expires && expires <= DateTime.UtcNow)
            return Error.Validation("The expiry date must be in the future.");

        var customSlug = customSlugRaw;

        for (var attempt = 0; attempt < (customSlug is null ? MaxSlugAttempts : 1); attempt++)
        {
            string slug;
            int? codeLength = null;
            long? codeNumber = null;
            if (customSlug is not null)
            {
                slug = customSlug;
            }
            else
            {
                var (length, number) = await leases.NextAsync(ct);
                var generated = codes.Encode(length, number);
                if (SlugRules.IsReserved(generated)) continue; // skipped per spec §7.2 step 6; the number is simply not reused for a slug
                slug = generated;
                codeLength = length;
                codeNumber = number;
            }

            // ---- Phase C: one transaction, no external calls inside it --------
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);

            if (idempotencyKey is not null)
            {
                var claimed = await TryClaimIdempotencyKeyAsync(scope, idempotencyKey, requestHash!, ct);
                if (claimed.AlreadyDone is { } stored)
                {
                    await tx.RollbackAsync(ct);
                    return stored.Mismatch
                        ? Error.Validation("idempotency-mismatch: this key was already used with a different request.")
                        : Result<LinkDto>.Ok(stored.Dto!);
                }
            }

            if (customSlug is not null)
            {
                // Serialise concurrent creators of the same alias (spec §7.2 step 10.2): without this,
                // two requests for the same brand-new alias could both pass the "does it exist" check.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended('zaagl.alias:' || {customSlug}, 0))", ct);

                // Spec §7.2 step 10.4: inspect any existing row for this slug, under the lock.
                var key = SlugRules.Key(customSlug);

                // Spec §7.15/R3a: a code taken down for abuse or by the safety check is never released,
                // even after purge, unless the takedown is reversed with the audited reinstate.
                if (await db.BlockedAliases.AnyAsync(a => a.SlugKey == key, ct))
                {
                    await tx.RollbackAsync(ct);
                    return Error.Conflict("alias-unavailable: this slug was taken down and is not available.");
                }

                var existingRow = await db.Links.Include(l => l.Policy)
                    .FirstOrDefaultAsync(l => l.SlugKey == key, ct);
                if (existingRow is not null)
                {
                    var existingState = LinkStateCalculator.Compute(
                        existingRow.PurgeAfter, existingRow.DeletedAt, existingRow.DisabledAt, existingRow.Policy?.ExpiresAt, DateTime.UtcNow);
                    if (existingState == LinkState.Purgeable)
                    {
                        db.Links.Remove(existingRow); // reclaim: the predecessor is purged inside this same transaction
                    }
                    else
                    {
                        await tx.RollbackAsync(ct);
                        return existingRow.OwnerId == userId
                            ? Error.Conflict("You already own this slug. Renew it instead of creating it again.")
                            : Error.Conflict("This slug is already taken.");
                    }
                }
            }

            var link = new Link
            {
                OwnerId = userId,
                Slug = slug,
                SlugKey = SlugRules.Key(slug),
                IsCustom = customSlug is not null,
                CodeLength = codeLength,
                CodeNumber = codeNumber,
                KeyVersion = customSlug is null ? CodeGenerator.KeyVersion : null,
            };
            ApplyInput(link, input, n);
            link.SafetyCheckedAt = n.SafetyChecked ? time.GetUtcNow().UtcDateTime : null;
            RecomputePurgeAfter(link);
            db.Links.Add(link);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                if (customSlug is not null)
                {
                    // Classify per spec §7.2 step 11: an alias conflict against another alias is a normal
                    // race (someone else just took it); against the generated-number index it would be an
                    // allocator integrity failure, which cannot happen here since this branch never sets
                    // CodeNumber - so any unique violation on this insert is the slug_key constraint.
                    return Error.Conflict("This slug is already taken.");
                }
                continue; // generated slug collided case-insensitively (SlugKey); take the next leased number
            }

            if (idempotencyKey is not null)
                await CompleteIdempotencyKeyAsync(scope, idempotencyKey, ToDto(link), ct);

            await tx.CommitAsync(ct);

            await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
            await invalidator.NotifyChangedAsync([link.SlugKey], ct);

            // ---- Phase D: response ---------------------------------------------
            return Result<LinkDto>.Ok(ToDto(link));
        }

        return Error.Conflict("Could not allocate a unique slug. Please try again.");
    }

    /// <summary>Spec §7.12: a guest creation (no account). <paramref name="creatorKey"/> identifies the
    /// caller (HMAC of its IP partition) and is also the idempotency scope; <paramref name="quotaChecks"/>
    /// is every prefix-level quota that applies (creator key, and for IPv6 also /56 and /48), each checked
    /// and incremented inside the same transaction as the insert, in key order, so two guest creations for
    /// the same partition serialise instead of racing past the limit.</summary>
    public async Task<Result<LinkDto>> CreateGuestAsync(
        byte[] creatorKey, IReadOnlyList<GuestQuotaCheck> quotaChecks, LinkInput input, CancellationToken ct,
        string? idempotencyKey = null)
    {
        var customSlugRaw = string.IsNullOrWhiteSpace(input.CustomSlug) ? null : input.CustomSlug.Trim();
        byte[]? requestHash = idempotencyKey is null ? null : ComputeRequestHash(input, customSlugRaw);
        var scope = $"g:{Convert.ToHexString(creatorKey)}";

        if (idempotencyKey is not null)
        {
            var existing = await db.IdempotencyKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.Scope == scope && k.Key == idempotencyKey, ct);
            if (existing is { Status: "done" })
            {
                return existing.RequestHash.AsSpan().SequenceEqual(requestHash)
                    ? Result<LinkDto>.Ok(System.Text.Json.JsonSerializer.Deserialize<LinkDto>(existing.ResponseBody!)!)
                    : Error.Validation("idempotency-mismatch: this key was already used with a different request.");
            }
        }

        if (!creationGate.CanCreate)
            return Error.ServiceUnavailable($"Link creation is temporarily unavailable ({creationGate.State}). Try again shortly.");

        var limits = PlanLimits.Guest;
        var prepared = await PrepareAsync(null, input, limits, isCreate: true, ct);
        if (prepared.Error is not null) return prepared.Error;
        var n = prepared.Value!;

        var now = time.GetUtcNow().UtcDateTime;
        var maxExpiresAt = now.AddDays(guestOptions.Value.ExpiryDays);
        // Spec §7.12: default and maximum 7 days; a shorter requested expiry is allowed, a longer one is not.
        DateTime expiresAt;
        if (input.Policy?.ExpiresAt is { } requested)
        {
            var requestedUtc = requested.ToUniversalTime();
            if (requestedUtc <= now) return Error.Validation("The expiry date must be in the future.");
            if (requestedUtc > maxExpiresAt) return Error.Validation($"Guest links cannot expire more than {guestOptions.Value.ExpiryDays} days from now.");
            expiresAt = requestedUtc;
        }
        else
        {
            expiresAt = maxExpiresAt;
        }

        var customSlug = customSlugRaw;

        for (var attempt = 0; attempt < (customSlug is null ? MaxSlugAttempts : 1); attempt++)
        {
            string slug;
            int? codeLength = null;
            long? codeNumber = null;
            if (customSlug is not null)
            {
                slug = customSlug;
            }
            else
            {
                var (length, number) = await leases.NextAsync(ct);
                var generated = codes.Encode(length, number);
                if (SlugRules.IsReserved(generated)) continue;
                slug = generated;
                codeLength = length;
                codeNumber = number;
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);

            // Spec §7.12: hard, transactional, checked before the insert - a rollback (slug collision,
            // idempotency replay) also rolls back any quota already claimed by this attempt.
            if (await GuestQuotaStore.CheckAndIncrementAllAsync(db, quotaChecks, now, ct) is { } exceeded)
            {
                await tx.RollbackAsync(ct);
                return Error.TooManyRequests($"quota: the guest link limit for this network ({exceeded}) has been reached.");
            }

            if (idempotencyKey is not null)
            {
                var claimed = await TryClaimIdempotencyKeyAsync(scope, idempotencyKey, requestHash!, ct);
                if (claimed.AlreadyDone is { } stored)
                {
                    await tx.RollbackAsync(ct);
                    return stored.Mismatch
                        ? Error.Validation("idempotency-mismatch: this key was already used with a different request.")
                        : Result<LinkDto>.Ok(stored.Dto!);
                }
            }

            if (customSlug is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended('zaagl.alias:' || {customSlug}, 0))", ct);

                var key = SlugRules.Key(customSlug);
                if (await db.BlockedAliases.AnyAsync(a => a.SlugKey == key, ct))
                {
                    await tx.RollbackAsync(ct);
                    return Error.Conflict("alias-unavailable: this slug was taken down and is not available.");
                }

                var existingRow = await db.Links.Include(l => l.Policy)
                    .FirstOrDefaultAsync(l => l.SlugKey == key, ct);
                if (existingRow is not null)
                {
                    var existingState = LinkStateCalculator.Compute(
                        existingRow.PurgeAfter, existingRow.DeletedAt, existingRow.DisabledAt, existingRow.Policy?.ExpiresAt, now);
                    if (existingState == LinkState.Purgeable)
                    {
                        db.Links.Remove(existingRow);
                    }
                    else
                    {
                        await tx.RollbackAsync(ct);
                        return Error.Conflict("This slug is already taken.");
                    }
                }
            }

            var link = new Link
            {
                OwnerId = null,
                GuestCreatorKey = creatorKey,
                Slug = slug,
                SlugKey = SlugRules.Key(slug),
                IsCustom = customSlug is not null,
                CodeLength = codeLength,
                CodeNumber = codeNumber,
                KeyVersion = customSlug is null ? CodeGenerator.KeyVersion : null,
            };
            ApplyInput(link, input, n);
            link.SafetyCheckedAt = n.SafetyChecked ? now : null;
            // Guests get a fixed expiry and no quarantine: purge_after = expires_at exactly (spec §7.12,
            // §1.1 "Quarantine after expiry: none"), overriding whatever ApplyInput set from input.Policy.
            var policy = link.Policy ??= new AccessPolicy { LinkId = link.Id };
            policy.ExpiresAt = expiresAt;
            link.PurgeAfter = expiresAt;
            db.Links.Add(link);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                if (customSlug is not null) return Error.Conflict("This slug is already taken.");
                continue;
            }

            if (idempotencyKey is not null)
                await CompleteIdempotencyKeyAsync(scope, idempotencyKey, ToDto(link), ct);

            await tx.CommitAsync(ct);

            await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
            await invalidator.NotifyChangedAsync([link.SlugKey], ct);

            return Result<LinkDto>.Ok(ToDto(link));
        }

        return Error.Conflict("Could not allocate a unique slug. Please try again.");
    }

    private sealed record ClaimOutcome((LinkDto? Dto, bool Mismatch)? AlreadyDone);

    private async Task<ClaimOutcome> TryClaimIdempotencyKeyAsync(string scope, string key, byte[] requestHash, CancellationToken ct)
    {
        // INSERT ... ON CONFLICT DO NOTHING: a row appearing means this request owns the key; no row means
        // another transaction already claimed it (it may still be "pending" - PostgreSQL made our insert
        // wait for that transaction's outcome, so by the time we reach here it is either "done" or gone).
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO links.idempotency_keys (scope, key, request_hash, status, created_at) VALUES ({scope}, {key}, {requestHash}, 'pending', {DateTime.UtcNow}) ON CONFLICT DO NOTHING", ct);
        if (inserted > 0) return new ClaimOutcome(null); // we own it, proceed

        var row = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Scope == scope && k.Key == key, ct);
        if (row is null) return new ClaimOutcome(null); // raced and lost, then the other rolled back; proceed as owner
        if (!row.RequestHash.AsSpan().SequenceEqual(requestHash)) return new ClaimOutcome((null, true));
        if (row.Status == "done")
            return new ClaimOutcome((System.Text.Json.JsonSerializer.Deserialize<LinkDto>(row.ResponseBody!), false));
        return new ClaimOutcome(null); // still pending somehow; proceed as owner (best effort for this phase)
    }

    private async Task CompleteIdempotencyKeyAsync(string scope, string key, LinkDto dto, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE links.idempotency_keys SET status = 'done', status_code = 201, response_body = {json}::jsonb WHERE scope = {scope} AND key = {key}", ct);
    }

    private static byte[] ComputeRequestHash(LinkInput input, string? customSlug)
    {
        // Spec §7.2 step 3's exact shape, adapted to this app's field names.
        var expiry = input.Policy?.ExpiresAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var material = $"{input.DestinationUrl}\n{customSlug ?? ""}\n{expiry}";
        return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
    }

    /// <summary>purge_after = expires_at + QuarantineDays for an owned link with an expiry; null (never
    /// purged by age) when there's no expiry at all, matching this app's existing "expiry is optional"
    /// design rather than the spec's "every link expires" (guests, where purge_after = expires_at exactly,
    /// are a future phase).</summary>
    private void RecomputePurgeAfter(Link link) =>
        link.PurgeAfter = link.Policy?.ExpiresAt is { } exp ? exp.AddDays(options.Value.QuarantineDays) : null;

    // ---- Update -----------------------------------------------------------

    public async Task<Result<LinkDto>> UpdateAsync(Guid userId, Guid id, LinkInput input, CancellationToken ct)
    {
        var link = await db.Links.Include(l => l.Policy).Include(l => l.Rules)
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId && l.DeletedAt == null, ct);
        if (link is null) return Error.NotFound("Link not found.");

        var limits = await plans.GetLimitsAsync(userId, ct);
        var prepared = await PrepareAsync(userId, input with { CustomSlug = null }, limits, isCreate: false, ct);
        if (prepared.Error is not null) return prepared.Error;

        if (input.Rules is not null) db.Destinations.RemoveRange(link.Rules);
        ApplyInput(link, input, prepared.Value!);
        link.SafetyCheckedAt = prepared.Value!.SafetyChecked ? time.GetUtcNow().UtcDateTime : null;
        RecomputePurgeAfter(link);
        link.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
        await invalidator.NotifyChangedAsync([link.SlugKey], ct);
        return Result<LinkDto>.Ok(ToDto(link));
    }

    /// <summary>Spec §7.4 renewal: forward-only, re-derives purge_after, never calls the coordinator,
    /// works during an alias restore freeze (there is none yet in this build). Only an Active or
    /// Quarantined link is renewable; see the spec §4.3 state table.</summary>
    public async Task<Result<LinkDto>> RenewAsync(Guid userId, Guid id, DateTime? requestedExpiresAt, CancellationToken ct)
    {
        var link = await db.Links.Include(l => l.Policy).Include(l => l.Rules)
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (link is null) return Error.NotFound("Link not found.");

        var now = time.GetUtcNow().UtcDateTime;
        var state = LinkStateCalculator.Compute(link.PurgeAfter, link.DeletedAt, link.DisabledAt, link.Policy?.ExpiresAt, now);
        switch (state)
        {
            case LinkState.Purgeable: return Error.NotFound("Link not found.");
            case LinkState.Deleted or LinkState.Disabled: return Error.Conflict("This link cannot be renewed.");
        }

        var currentExpiry = link.Policy?.ExpiresAt;
        var limits = await plans.GetLimitsAsync(userId, ct);
        var newExpiry = (requestedExpiresAt ?? now.AddDays(limits.DefaultExpiryDays)).ToUniversalTime();

        if (currentExpiry is { } current && newExpiry <= current)
            return Error.Validation("renewal-must-extend: the new expiry must be later than the current one.");
        if (newExpiry <= now)
            return Error.Validation("The new expiry must be in the future.");
        if (limits.MaxExpiryDays is { } maxDays && newExpiry > now.AddDays(maxDays))
            return Error.Validation($"The new expiry cannot be more than {maxDays} days from now.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);

        var policy = link.Policy ??= new AccessPolicy { LinkId = link.Id };
        policy.ExpiresAt = newExpiry;
        link.PurgeAfter = newExpiry.AddDays(options.Value.QuarantineDays);
        link.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
        await invalidator.NotifyChangedAsync([link.SlugKey], ct);
        return Result<LinkDto>.Ok(ToDto(link));
    }

    public async Task<Result<LinkDto>> SetFlagsAsync(Guid userId, Guid id, LinkFlagsInput flags, CancellationToken ct)
    {
        var link = await db.Links.Include(l => l.Policy).Include(l => l.Rules)
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId && l.DeletedAt == null, ct);
        if (link is null) return Error.NotFound("Link not found.");

        // IsActive is this app's pre-existing on/off switch; it's kept as a plain synonym for the spec's
        // "user-disabled" kind (§4.3) rather than a second, independent liveness signal - turning it off
        // sets DisabledAt/DisabledKind, turning it on clears them, but only when the link was disabled by
        // this same switch (an admin/abuse/safety disable, once those exist, must not be undone by it).
        if (flags.IsActive is { } active)
        {
            link.IsActive = active;
            if (!active)
            {
                link.DisabledAt = time.GetUtcNow().UtcDateTime;
                link.DisabledKind = DisabledKinds.UserDisabled;
            }
            else if (link.DisabledKind == DisabledKinds.UserDisabled)
            {
                link.DisabledAt = null;
                link.DisabledKind = null;
                link.DisabledReason = null;
            }
        }
        if (flags.PublicStats is { } publicStats) link.PublicStats = publicStats;
        link.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
        await invalidator.NotifyChangedAsync([link.SlugKey], ct);
        return Result<LinkDto>.Ok(ToDto(link));
    }

    // ---- Read / delete ----------------------------------------------------

    public async Task<LinkDto?> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var link = await db.Links.AsNoTracking().Include(l => l.Policy).Include(l => l.Rules)
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId && l.DeletedAt == null, ct);
        return link is null ? null : ToDto(link);
    }

    public async Task<PagedResult<LinkDto>> ListAsync(Guid userId, ListLinksQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // Deleted links are owner-invisible per spec §4.3's state table ("Visible to owner: no").
        var q = db.Links.AsNoTracking().Where(l => l.OwnerId == userId && l.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EscapeLike(query.Search.Trim())}%";
            q = q.Where(l => EF.Functions.ILike(l.Slug, pattern)
                || EF.Functions.ILike(l.DestinationUrl, pattern)
                || (l.Title != null && EF.Functions.ILike(l.Title, pattern)));
        }
        if (query.FolderId is { } folderId) q = q.Where(l => l.FolderId == folderId);
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim();
            q = q.Where(l => l.Tags.Contains(tag));
        }

        var now = DateTime.UtcNow;
        // "Expired" means the link no longer redirects: deactivated, past its date, or out of clicks.
        switch (query.Status?.ToLowerInvariant())
        {
            case "expired":
                q = q.Where(l => !l.IsActive
                    || (l.Policy != null && ((l.Policy.ExpiresAt != null && l.Policy.ExpiresAt <= now)
                        || (l.Policy.MaxClicks != null && l.Policy.ClicksUsed >= l.Policy.MaxClicks))));
                break;
            case "active":
            case "password":
                q = q.Where(l => l.IsActive
                    && (l.Policy == null || ((l.Policy.ExpiresAt == null || l.Policy.ExpiresAt > now)
                        && (l.Policy.MaxClicks == null || l.Policy.ClicksUsed < l.Policy.MaxClicks))));
                if (query.Status!.Equals("password", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(l => l.Policy != null && l.Policy.PasswordHash != null);
                break;
        }

        var total = await q.CountAsync(ct);
        var links = await q.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(l => l.Policy).Include(l => l.Rules)
            .ToListAsync(ct);

        return new(links.Select(ToDto).ToList(), total, page, pageSize);
    }

    /// <summary>Spec §4.3: delete is soft. The row (and its alias/slug) stays reserved until purge_after,
    /// so nobody can take over a just-deleted alias immediately; an admin can still see deleted links
    /// (not built in this phase). purge_after only ever moves earlier here, never later.</summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var link = await db.Links.Include(l => l.Policy)
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId && l.DeletedAt == null, ct);
        if (link is null) return false;

        var now = time.GetUtcNow().UtcDateTime;
        link.DeletedAt = now;
        var shortenedPurge = now.AddDays(options.Value.QuarantineDays);
        link.PurgeAfter = link.PurgeAfter is { } existing && existing < shortenedPurge ? existing : shortenedPurge;
        link.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
        await invalidator.NotifyChangedAsync([link.SlugKey], ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(Guid userId, CancellationToken ct)
    {
        var all = await db.Links.AsNoTracking().Where(l => l.OwnerId == userId && l.Tags.Length > 0)
            .Select(l => l.Tags).ToListAsync(ct);
        return all.SelectMany(t => t).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<SlugAvailabilityDto> CheckSlugAsync(string? slug, CancellationToken ct)
    {
        if (!SlugRules.IsValidFormat(slug))
            return new(false, $"Slug must be {SlugRules.MinCustomLength}-{SlugRules.MaxLength} characters: letters, digits, '-' or '_'.");
        if (SlugRules.IsReserved(slug!) || codes.IsGeneratorReachable(slug!)) return new(false, "This slug is reserved.");

        // Spec §4.3: "an alias is free when no row has that code, OR the row is purgeable" - a deleted
        // link whose quarantine hasn't ended yet still holds its slug.
        var now = time.GetUtcNow().UtcDateTime;
        var key = SlugRules.Key(slug!);
        var row = await db.Links.AsNoTracking()
            .Where(l => l.SlugKey == key)
            .Select(l => new { l.PurgeAfter, l.DeletedAt, l.DisabledAt, Expires = l.Policy != null ? l.Policy.ExpiresAt : null })
            .FirstOrDefaultAsync(ct);
        if (row is null) return new(true, null);

        var state = LinkStateCalculator.Compute(row.PurgeAfter, row.DeletedAt, row.DisabledAt, row.Expires, now);
        return state == LinkState.Purgeable ? new(true, null) : new(false, "This slug is already taken.");
    }

    // ---- Internals --------------------------------------------------------

    private async Task<Result<Normalized>> PrepareAsync(
        Guid? userId, LinkInput input, PlanLimits limits, bool isCreate, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        if (!validation.IsValid)
            return Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage).Distinct()));

        if (CheckPlan(input, limits) is { } planError) return planError;

        var normalized = Normalize(input);
        if (normalized.Error is not null) return normalized.Error;
        var n = normalized.Value!;

        if (input.FolderId is { } folderId)
        {
            // Spec §7.12: guest links have no owner, so they can never reference a folder.
            if (userId is not { } uid) return Error.Validation("Guests cannot use folders.");
            if (!await db.Folders.AnyAsync(f => f.Id == folderId && f.OwnerId == uid, ct))
                return Error.Validation("Folder not found.");
        }

        if (await blocklist.IsBlockedAsync(n.TargetHost, n.Destination, ct))
            return Error.Validation("destination-blocked: this destination is not allowed.");

        var urls = n.Rules.Select(r => r.Url).Append(n.Destination).Distinct();
        var allChecked = true;
        foreach (var url in urls)
        {
            var result = await reputation.CheckAsync(url, ct);
            if (!result.IsSafe)
                return Error.Validation($"destination-unsafe: this URL was flagged as unsafe ({FriendlyThreat(result.Threat)}) and cannot be shortened.");
            allChecked &= result.Checked;
        }

        return Result<Normalized>.Ok(n with { SafetyChecked = allChecked });
    }

    private static Error? CheckPlan(LinkInput input, PlanLimits limits)
    {
        if (!string.IsNullOrWhiteSpace(input.CustomSlug) && !limits.CustomSlugs)
            return Error.PlanRequired("Custom slugs need the Starter plan.");

        if (limits.AdvancedGates) return null;
        if (!string.IsNullOrEmpty(input.Policy?.Password))
            return Error.PlanRequired("Password protection needs the Starter plan.");
        if (input.Policy?.MaxClicks is not null)
            return Error.PlanRequired("Click limits need the Starter plan.");
        if (input.Rules is { Length: > 0 })
            return Error.PlanRequired("Device, country and A/B routing need the Starter plan.");
        return null;
    }

    private Result<Normalized> Normalize(LinkInput input)
    {
        if (!UrlSafety.TryNormalize(input.DestinationUrl, _ownHosts, out var destination, out var error))
            return Error.Validation($"Destination URL: {error}");

        string? fallback = null;
        if (!string.IsNullOrWhiteSpace(input.Policy?.FallbackUrl))
        {
            if (!UrlSafety.TryNormalize(input.Policy.FallbackUrl, _ownHosts, out fallback, out error))
                return Error.Validation($"Fallback URL: {error}");
        }

        string? ogImage = null;
        if (!string.IsNullOrWhiteSpace(input.OpenGraph?.ImageUrl))
        {
            if (!UrlSafety.TryNormalize(input.OpenGraph.ImageUrl, [], out ogImage, out error))
                return Error.Validation($"Preview image URL: {error}");
        }

        var rules = new List<RuleInput>();
        foreach (var rule in input.Rules ?? [])
        {
            if (!UrlSafety.TryNormalize(rule.Url, _ownHosts, out var url, out error))
                return Error.Validation($"Routing rule URL: {error}");

            var kind = rule.Kind.ToLowerInvariant();
            var match = kind switch
            {
                RuleKinds.Device => rule.Match?.ToLowerInvariant(),
                RuleKinds.Country => rule.Match?.ToUpperInvariant(),
                _ => null,
            };
            rules.Add(new RuleInput(kind, match, url, rule.Weight, rule.Priority));
        }
        if (rules.Count(r => r.Kind == RuleKinds.Split) == 1)
            return Error.Validation("A/B split testing needs at least two variants.");

        var tags = (input.Tags ?? [])
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var targetHost = new Uri(destination).IdnHost.TrimEnd('.').ToLowerInvariant();
        return Result<Normalized>.Ok(new(destination, fallback, ogImage, rules, tags, targetHost, SafetyChecked: true));
    }

    private static void ApplyInput(Link link, LinkInput input, Normalized n)
    {
        link.DestinationUrl = n.Destination;
        link.TargetHost = n.TargetHost;
        link.Title = Clean(input.Title);
        link.Description = Clean(input.Description);
        link.FolderId = input.FolderId;
        link.PublicStats = input.PublicStats;
        link.IsActive = input.IsActive;
        if (input.Tags is not null) link.Tags = n.Tags;

        if (input.OpenGraph is { } og)
        {
            link.OgTitle = Clean(og.Title);
            link.OgDescription = Clean(og.Description);
            link.OgImageUrl = n.OgImage;
        }

        if (input.Policy is { } p)
        {
            var policy = link.Policy ??= new AccessPolicy { LinkId = link.Id };
            policy.ExpiresAt = p.ExpiresAt?.ToUniversalTime();
            policy.MaxClicks = p.MaxClicks;
            policy.FallbackUrl = n.Fallback;
            if (!string.IsNullOrEmpty(p.Password)) policy.PasswordHash = BCrypt.Net.BCrypt.HashPassword(p.Password);
            else if (p.ClearPassword) policy.PasswordHash = null;
        }

        if (input.Rules is not null)
        {
            link.Rules = n.Rules.Select(r => new LinkDestination
            {
                LinkId = link.Id,
                Kind = r.Kind,
                Match = r.Match,
                Url = r.Url,
                Weight = r.Weight,
                Priority = r.Priority,
            }).ToList();
        }
    }

    // "Strategy 6": lease the next number from the coordinator (one network call per block of leased
    // numbers, not per link) and scramble it so consecutive leases don't look consecutive. Uniqueness
    // is guaranteed by the lease itself (no two callers are ever given the same number) and, as a
    // belt-and-braces check against a reserved word or a clash with a custom slug, by the database's
    // unique index on slug_key - the same safety net CreateAsync already relies on for a retry.
    private async Task<string?> NextGeneratedSlugAsync(CancellationToken ct)
    {
        var (length, next) = await leases.NextAsync(ct);
        var slug = codes.Encode(length, next);
        return SlugRules.IsReserved(slug) ? null : slug;
    }

    public static LinkDto ToDto(Link l) => new(
        l.Id,
        l.Slug,
        l.DestinationUrl,
        l.Title,
        l.Description,
        l.FolderId,
        l.Tags,
        l.IsActive,
        l.PublicStats,
        new OpenGraphDto(l.OgTitle, l.OgDescription, l.OgImageUrl),
        new PolicyDto(
            l.Policy?.PasswordHash is not null,
            l.Policy?.ExpiresAt,
            l.Policy?.MaxClicks,
            l.Policy?.ClicksUsed ?? 0,
            l.Policy?.FallbackUrl),
        l.Rules.OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .Select(r => new RuleDto(r.Kind, r.Match, r.Url, r.Weight, r.Priority)).ToList(),
        l.CreatedAt,
        l.UpdatedAt,
        l.OwnerId is null);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string FriendlyThreat(string? threat) => threat switch
    {
        "MALWARE" => "malware",
        "SOCIAL_ENGINEERING" => "phishing",
        "UNWANTED_SOFTWARE" => "unwanted software",
        _ => "malicious content",
    };

    private static IReadOnlyCollection<string> OwnHosts(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return [];
        var host = uri.IdnHost.ToLowerInvariant();
        return [host, host.StartsWith("www.") ? host[4..] : $"www.{host}"];
    }
}
