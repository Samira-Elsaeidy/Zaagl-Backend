using System.Buffers.Binary;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Resources.Domain;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Security;

namespace Zaagl.Modules.Resources.Application;

/// <summary>Configuration section <c>Content</c> (ARC-008 §3, §4): ticket keys and lifetimes.</summary>
public sealed class ContentOptions
{
    public const string Section = "Content";

    /// <summary>Rotating key set; the first entry signs, every entry verifies. Keys are base64, 32 bytes or more.</summary>
    public List<HmacKeyEntry> TicketKeys { get; set; } = [];

    /// <summary>Base of the ticket URL (C3). The host never comes from the request.</summary>
    public string UserContentBaseUrl { get; set; } = "https://usercontent.zaa.gl";

    /// <summary>Raw, owner, inline and preview tickets (ARC-008 §3).</summary>
    public int InlineTicketMinutes { get; set; } = 15;

    /// <summary>Download tickets are valid this long while unused (ARC-008 §3, <c>Content:DownloadTicketSeconds</c>).</summary>
    public int DownloadTicketSeconds { get; set; } = 120;

    /// <summary>ARC-006 §5 step 5: a used non-burn Download ticket keeps serving Range parts this long after its first GET.</summary>
    public int DownloadGrantReuseMinutes { get; set; } = 30;

    /// <summary>ARC-006 §5 step 5: and for at most this many requests in total.</summary>
    public int DownloadGrantMaxRequests { get; set; } = 16;

    /// <summary>Per-response cap (PD-38, to confirm: proposed 90,000,000). Larger objects need exactly one byte range.</summary>
    public long MaxResponseBytes { get; set; } = 90_000_000;

    /// <summary>Origin allowed to fetch Inline/Preview responses cross-origin (ARC-008 §9, <c>X-Zaagl-Acao</c>).</summary>
    public string AppOrigin { get; set; } = "https://app.zaa.gl";

    /// <summary>Development and tests only: the hop streams object bytes itself (no Nginx X-Accel-Redirect on this machine).</summary>
    public bool DirectObjectServing { get; set; }

    /// <summary>Detected types that may open inline (ARC-008 §7, PD-16 OPEN). Anything else is an attachment.</summary>
    public List<string> InlineMimeAllowlist { get; set; } = ["image/png", "image/jpeg", "image/gif", "image/webp"];
}

/// <summary>Configuration section <c>Burn</c> (PD-05, ARC-019 §6): the grace after a claim.</summary>
public sealed class BurnOptions
{
    public const string Section = "Burn";

    /// <summary>The claiming grant may keep requesting for this long after the claim (PD-05: 30 minutes).</summary>
    public int GraceMinutes { get; set; } = 30;

    /// <summary>At most this many requests in total, the claiming one included (PD-05: 16).</summary>
    public int MaxRequests { get; set; } = 16;
}

/// <summary>The ticket key ring, built once at startup (singleton). Domain <c>zaagl/ticket/v1</c> keeps tickets and
/// access grants apart (ARC-008 §3).</summary>
public sealed class TicketKeyRing(HmacKeyRing ring)
{
    public const string Domain = "zaagl/ticket/v1\n";

    public HmacKeyRing Ring { get; } = ring;
}

/// <summary>
/// C2 <see cref="IDownloadGrants"/>: Raw, Owner, Inline, Preview (a named derivative of an image, S3) and Download tickets, and the burn claim (ARC-019). Preview tickets arrived
/// with the derivatives (S3); the delivery hop checks that the derivative is ready and the original clean.
/// <see cref="AuthorizeAsync"/> is read-only for a Download ticket: it validates and says what must be recorded
/// (<see cref="DeliveryDecision.Consume"/>); the hop records it with <see cref="ConsumeAsync"/> only after the object, size and
/// Range checks, so a refused request consumes nothing (ARC-019 section 5).
/// Ticket wire format: ARC-008 §3. The ticket does not carry the link key or password: both were checked before
/// <see cref="MintAsync"/> (callers pass IAccessGate or the owner check first). The User-Agent is never read here.
/// </summary>
public sealed class DownloadGrants(
    ResourcesDbContext db,
    ILinkSnapshots links,
    TicketKeyRing keys,
    IOptions<ContentOptions> options,
    IOptions<BurnOptions> burn,
    TimeProvider time) : IDownloadGrants
{
    private const byte Version = 0x01;
    private const int MaxDerivativeLength = 32;

    public async Task<Result<DeliveryTicket>> MintAsync(MintTicketRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (request.Purpose is not (DeliveryPurpose.Raw or DeliveryPurpose.Owner or DeliveryPurpose.Inline or DeliveryPurpose.Download or DeliveryPurpose.Preview))
            return Error.Validation("This ticket purpose is not available yet.");

        var row = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.ResourceId, ct);
        if (row is null) return Error.NotFound("Resource not found.");

        // Snippet tickets (Raw, Owner) name the version (v<n>); tickets for stored objects never carry a derivative.
        if (row.Kind == ResourceKind.Snippet)
        {
            if (request.Purpose is not (DeliveryPurpose.Raw or DeliveryPurpose.Owner))
                return Error.Validation("This ticket purpose does not apply to a snippet.");
            if (string.IsNullOrEmpty(request.Derivative) || request.Derivative.Length > MaxDerivativeLength || !request.Derivative.All(char.IsAsciiLetterOrDigit))
                return Error.Validation("A raw ticket needs a derivative such as v1.");
        }
        else
        {
            if (request.Purpose == DeliveryPurpose.Raw) return Error.Validation("Raw tickets are for snippets.");
            // Preview tickets (S3) name an explicit derivative kind such as thumb-320; every other ticket is for the original.
            if (request.Purpose == DeliveryPurpose.Preview)
            {
                if (string.IsNullOrEmpty(request.Derivative) || request.Derivative.Length > MaxDerivativeLength
                    || !request.Derivative.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return Error.Validation("A preview ticket needs a derivative kind.");
            }
            else if (!string.IsNullOrEmpty(request.Derivative)) return Error.Validation("Original downloads carry no derivative.");
        }

        if (row.ProcessingStatus != ProcessingStatus.Ready) return Error.Of(ErrorCodes.Processing, "This content is not ready.");
        // Burn files get no inline ticket (addendum §8.1.3). A claimed burn file cannot start a new download: the claiming client
        // holds its own ticket (ARC-019 section 4).
        if (row.BurnAfterDownload && request.Purpose is DeliveryPurpose.Inline or DeliveryPurpose.Preview) return Error.Conflict("Burn files have no inline view.");
        if (row.BurnClaimedAt is not null && request.Purpose == DeliveryPurpose.Download)
            return Error.Of(ErrorCodes.Burned, "This content was downloaded and deleted.");

        var now = time.GetUtcNow().UtcDateTime;
        var expires = request.Purpose == DeliveryPurpose.Download
            ? now.AddSeconds(Math.Max(10, options.Value.DownloadTicketSeconds))
            : now.AddMinutes(Math.Max(1, options.Value.InlineTicketMinutes));
        var grant = new DownloadGrantRow
        {
            Id = Guid.CreateVersion7(),
            ResourceId = row.Id,
            Purpose = request.Purpose,
            MintedAt = now,
            FirstUseBy = expires,
        };
        db.DownloadGrants.Add(grant);
        await db.SaveChangesAsync(ct);

        var token = keys.Ring.Sign(WritePayload(keys.Ring.SigningKid, grant.Id, row.Id, request.Purpose, request.Derivative ?? "", expires), keys.Ring.SigningKid);
        var url = $"{options.Value.UserContentBaseUrl.TrimEnd('/')}/d/{token}";
        return Result<DeliveryTicket>.Ok(new DeliveryTicket(url, expires));
    }

    public async Task<DeliveryDecision> AuthorizeAsync(string ticket, bool isHead, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var invalid = new DeliveryDecision(DeliveryOutcome.TicketInvalid, null, null, null);

        if (HmacKeyRing.Split(ticket) is not { } parts) return invalid;
        if (ReadPayload(parts.Payload) is not { } claims || !keys.Ring.VerifyMac(claims.KeyId, parts.Payload, parts.Mac)) return invalid;
        var now = time.GetUtcNow().UtcDateTime;
        if (claims.Purpose is not (DeliveryPurpose.Raw or DeliveryPurpose.Owner or DeliveryPurpose.Inline or DeliveryPurpose.Download or DeliveryPurpose.Preview)) return invalid;
        // A Download ticket's lifetime is its grant row (unused: first_use_by; used: the reuse or grace window), checked below.
        if (claims.Purpose != DeliveryPurpose.Download && now >= claims.ExpiresAt) return invalid;

        var grant = await db.DownloadGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == claims.GrantId, ct);
        if (grant is null || grant.ResourceId != claims.ResourceId || grant.Purpose != claims.Purpose) return invalid;

        var row = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == claims.ResourceId, ct);
        if (row is null) return invalid;

        // ARC-006 §2: the authoritative link state, read fresh so a takedown or delete is seen at once.
        LinkState? linkState = null;
        if (row.LinkId is { } linkId && await links.GetSnapshotByIdAsync(linkId, ct) is { } snapshot)
        {
            linkState = LinkStateCalculator.Compute(snapshot, now);
            if (linkState == LinkState.Active && !snapshot.IsActive) linkState = LinkState.Disabled;
        }
        var state = ResourceStateCalculator.Compute(linkState, row.ProcessingStatus, row.BurnClaimedAt, row.DeletedAt);

        if (claims.Purpose == DeliveryPurpose.Download) return AuthorizeDownload(grant, row, state, isHead, now);

        var outcome = state switch
        {
            ResourceState.Ready => DeliveryOutcome.Allowed,
            ResourceState.Processing => DeliveryOutcome.Processing,
            ResourceState.Rejected => DeliveryOutcome.Rejected,
            ResourceState.Burned => DeliveryOutcome.Burned,
            _ => DeliveryOutcome.Gone,
        };
        if (outcome != DeliveryOutcome.Allowed) return new DeliveryDecision(outcome, row.Id, claims.Purpose, null, claims.Derivative);

        if (!isHead) // HEAD never counts (ARC-008 §4.3)
        {
            await db.DownloadGrants.Where(g => g.Id == grant.Id).ExecuteUpdateAsync(s => s
                .SetProperty(g => g.RequestCount, g => g.RequestCount + 1)
                .SetProperty(g => g.FirstUsedAt, g => g.FirstUsedAt ?? now), ct);
        }
        // A burn file never holds an Inline ticket (refused at mint); a defensive check against a stale row.
        if (row.BurnAfterDownload && claims.Purpose is DeliveryPurpose.Inline or DeliveryPurpose.Preview) return invalid;
        var disposition = claims.Purpose is DeliveryPurpose.Inline or DeliveryPurpose.Preview ? DeliveryDisposition.Inline : DeliveryDisposition.Attachment;
        return new DeliveryDecision(DeliveryOutcome.Allowed, row.Id, claims.Purpose, disposition, claims.Derivative);
    }

    /// <summary>ARC-019 section 5 steps 1 to 3 and 6 for a Download ticket: read-only. The claim itself is step 7.</summary>
    private DeliveryDecision AuthorizeDownload(DownloadGrantRow grant, ResourceRow row, ResourceState state, bool isHead, DateTime now)
    {
        var d = DeliveryPurpose.Download;
        DeliveryDecision Refuse(DeliveryOutcome outcome) => new(outcome, outcome == DeliveryOutcome.TicketInvalid ? null : row.Id, d, null);
        DeliveryDecision Allow(DeliveryConsume mode) =>
            new(DeliveryOutcome.Allowed, row.Id, d, DeliveryDisposition.Attachment, null, isHead ? DeliveryConsume.None : mode, grant.Id);

        var claiming = row.BurnAfterDownload && row.BurnGrantId == grant.Id && grant.IsBurnClaim;
        switch (state)
        {
            case ResourceState.Burned when claiming:
                // The grace replaces the ticket expiry for the claiming grant (ARC-019 section 6); the MAC was verified above.
                return WithinGrace(row.BurnClaimedAt!.Value, grant.RequestCount, now) ? Allow(DeliveryConsume.BurnContinue) : Refuse(DeliveryOutcome.Burned);
            case ResourceState.Burned: return Refuse(DeliveryOutcome.Burned);
            case ResourceState.Processing: return Refuse(DeliveryOutcome.Processing);
            case ResourceState.Rejected: return Refuse(DeliveryOutcome.Rejected);
            case not ResourceState.Ready: return Refuse(DeliveryOutcome.Gone);
        }

        if (row.BurnAfterDownload)
        {
            // Unclaimed: only a fresh, unused ticket can claim; a ticket cannot have been used without claiming.
            if (grant.FirstUsedAt is not null || now >= grant.FirstUseBy) return Refuse(DeliveryOutcome.TicketInvalid);
            return Allow(DeliveryConsume.BurnClaim);
        }
        if (grant.FirstUsedAt is null)
            return now < grant.FirstUseBy ? Allow(DeliveryConsume.FirstUse) : Refuse(DeliveryOutcome.TicketInvalid);
        var reuse = TimeSpan.FromMinutes(Math.Max(1, options.Value.DownloadGrantReuseMinutes));
        return now < grant.FirstUsedAt.Value + reuse && grant.RequestCount < Math.Max(1, options.Value.DownloadGrantMaxRequests)
            ? Allow(DeliveryConsume.Continue)
            : Refuse(DeliveryOutcome.TicketInvalid);
    }

    private bool WithinGrace(DateTime claimedAt, int requestCount, DateTime now) =>
        now < claimedAt.AddMinutes(Math.Max(1, burn.Value.GraceMinutes)) && requestCount < Math.Max(1, burn.Value.MaxRequests);

    public async Task<DeliveryDecision> ConsumeAsync(DeliveryDecision decision, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (decision.Outcome != DeliveryOutcome.Allowed || decision.Consume == DeliveryConsume.None) return decision;
        if (decision.GrantId is not { } grantId || decision.ResourceId is not { } resourceId)
            return new DeliveryDecision(DeliveryOutcome.TicketInvalid, null, null, null);
        var now = time.GetUtcNow().UtcDateTime;
        var refusedInvalid = new DeliveryDecision(DeliveryOutcome.TicketInvalid, null, DeliveryPurpose.Download, null);

        switch (decision.Consume)
        {
            case DeliveryConsume.FirstUse:
            {
                // One conditional UPDATE sets first_used_at (S-2); the winner also counts the download in the same transaction.
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var first = await db.DownloadGrants
                    .Where(g => g.Id == grantId && g.FirstUsedAt == null && g.FirstUseBy > now)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.FirstUsedAt, now).SetProperty(g => g.RequestCount, 1), ct);
                if (first == 1)
                {
                    await db.Resources.Where(r => r.Id == resourceId)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.DownloadCount, r => r.DownloadCount + 1), ct);
                    await tx.CommitAsync(ct);
                    return decision;
                }
                await tx.RollbackAsync(ct);
                // A parallel first use of the same ticket won: this request is a reuse.
                return await CountReuseAsync(grantId, now, ct) ? decision : refusedInvalid;
            }
            case DeliveryConsume.Continue:
                return await CountReuseAsync(grantId, now, ct) ? decision : refusedInvalid;
            case DeliveryConsume.BurnClaim:
                return await ClaimAsync(decision, grantId, resourceId, now, ct);
            case DeliveryConsume.BurnContinue:
            {
                var claimedAt = await db.Resources.AsNoTracking().Where(r => r.Id == resourceId && r.BurnGrantId == grantId)
                    .Select(r => r.BurnClaimedAt).FirstOrDefaultAsync(ct);
                if (claimedAt is null) return new DeliveryDecision(DeliveryOutcome.Burned, resourceId, DeliveryPurpose.Download, null);
                return await CountBurnContinuationAsync(grantId, claimedAt.Value, now, ct)
                    ? decision
                    : new DeliveryDecision(DeliveryOutcome.Burned, resourceId, DeliveryPurpose.Download, null);
            }
            default:
                return decision;
        }
    }

    private async Task<bool> CountReuseAsync(Guid grantId, DateTime now, CancellationToken ct)
    {
        var windowStart = now.AddMinutes(-Math.Max(1, options.Value.DownloadGrantReuseMinutes));
        var max = Math.Max(1, options.Value.DownloadGrantMaxRequests);
        return await db.DownloadGrants
            .Where(g => g.Id == grantId && g.FirstUsedAt != null && g.FirstUsedAt > windowStart && g.RequestCount < max)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RequestCount, g => g.RequestCount + 1), ct) == 1;
    }

    private async Task<bool> CountBurnContinuationAsync(Guid grantId, DateTime claimedAt, DateTime now, CancellationToken ct)
    {
        var max = Math.Max(1, burn.Value.MaxRequests);
        var graceEnds = claimedAt.AddMinutes(Math.Max(1, burn.Value.GraceMinutes));
        return now < graceEnds && await db.DownloadGrants
            .Where(g => g.Id == grantId && g.IsBurnClaim && g.RequestCount < max)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RequestCount, g => g.RequestCount + 1), ct) == 1;
    }

    /// <summary>
    /// ARC-006 section 5 step 2, the only place the claim statements exist: one transaction, <c>lock_timeout 3s</c>, the resource row
    /// first, then the grant row; both must change exactly one row. Raw Npgsql on its own connection (R12). Zero rows on the first
    /// statement is classified by a plain SELECT: the same grant (a parallel request of the same ticket) is a continuation, anything
    /// else is burned or gone.
    /// </summary>
    private async Task<DeliveryDecision> ClaimAsync(DeliveryDecision decision, Guid grantId, Guid resourceId, DateTime now, CancellationToken ct)
    {
        DeliveryDecision Outcome(DeliveryOutcome o) => new(o, resourceId, DeliveryPurpose.Download, null);
        var connectionString = db.Database.GetConnectionString() ?? throw new InvalidOperationException("The resources connection string is not available.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '3s'", connection, tx))
                await timeout.ExecuteNonQueryAsync(ct);

            int claimed;
            await using (var claim = new NpgsqlCommand(
                """
                UPDATE resources.resources
                SET burn_claimed_at = @now, burn_grant_id = @grant, download_count = download_count + 1, updated_at = @now
                WHERE id = @rid AND burn_after_download AND burn_claimed_at IS NULL AND processing_status = 2 AND deleted_at IS NULL
                  AND link_expires_at > @now
                """, connection, tx))
            {
                claim.Parameters.AddWithValue("now", now);
                claim.Parameters.AddWithValue("grant", grantId);
                claim.Parameters.AddWithValue("rid", resourceId);
                claimed = await claim.ExecuteNonQueryAsync(ct);
            }

            if (claimed == 1)
            {
                int marked;
                await using (var grant = new NpgsqlCommand(
                    """
                    UPDATE resources.download_grants
                    SET first_used_at = @now, request_count = 1, is_burn_claim = true
                    WHERE id = @grant AND first_used_at IS NULL AND @now < first_use_by
                    """, connection, tx))
                {
                    grant.Parameters.AddWithValue("now", now);
                    grant.Parameters.AddWithValue("grant", grantId);
                    marked = await grant.ExecuteNonQueryAsync(ct);
                }
                if (marked != 1)
                {
                    await tx.RollbackAsync(ct); // both statements must change one row, else nothing is claimed
                    return Outcome(DeliveryOutcome.TicketInvalid);
                }
                await tx.CommitAsync(ct);
                return decision;
            }

            // Zero rows: somebody else holds the claim, or the file stopped being claimable. Classify with a plain SELECT.
            Guid? claimant = null;
            DateTime? claimedAt = null;
            bool deleted = false, expired = false, found = false;
            await using (var read = new NpgsqlCommand(
                "SELECT burn_grant_id, burn_claimed_at, deleted_at IS NOT NULL, link_expires_at <= @now FROM resources.resources WHERE id = @rid", connection, tx))
            {
                read.Parameters.AddWithValue("now", now);
                read.Parameters.AddWithValue("rid", resourceId);
                await using var reader = await read.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    found = true;
                    claimant = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                    claimedAt = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                    deleted = reader.GetBoolean(2);
                    expired = reader.GetBoolean(3);
                }
            }
            if (!found || deleted) { await tx.RollbackAsync(ct); return Outcome(DeliveryOutcome.Gone); }
            if (claimedAt is { } claimTime)
            {
                if (claimant != grantId) { await tx.RollbackAsync(ct); return Outcome(DeliveryOutcome.Burned); }
                // P2: the same ticket twice at once. The loser is a continuation, counted by the guarded statement of step 3.
                int counted;
                await using (var again = new NpgsqlCommand(
                    """
                    UPDATE resources.download_grants SET request_count = request_count + 1
                    WHERE id = @grant AND is_burn_claim AND request_count < @max AND @claimed > @cutoff
                    """, connection, tx))
                {
                    again.Parameters.AddWithValue("grant", grantId);
                    again.Parameters.AddWithValue("max", Math.Max(1, burn.Value.MaxRequests));
                    again.Parameters.AddWithValue("claimed", claimTime);
                    again.Parameters.AddWithValue("cutoff", now.AddMinutes(-Math.Max(1, burn.Value.GraceMinutes)));
                    counted = await again.ExecuteNonQueryAsync(ct);
                }
                if (counted == 1) { await tx.CommitAsync(ct); return decision; }
                await tx.RollbackAsync(ct);
                return Outcome(DeliveryOutcome.Burned);
            }
            await tx.RollbackAsync(ct);
            return Outcome(expired ? DeliveryOutcome.Gone : DeliveryOutcome.Processing);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            // The transaction aborted: nothing was consumed. The caller answers 503 busy, Retry-After 1.
            return Outcome(DeliveryOutcome.Busy);
        }
    }

    // ---- wire format (ARC-008 §3), big-endian ----------------------------------------------------------

    private static byte[] WritePayload(string kid, Guid grantId, Guid resourceId, DeliveryPurpose purpose, string derivative, DateTime expires)
    {
        var kidBytes = Encoding.ASCII.GetBytes(kid);
        var derivativeBytes = Encoding.ASCII.GetBytes(derivative);
        var payload = new byte[1 + 1 + kidBytes.Length + 16 + 16 + 1 + 1 + derivativeBytes.Length + 8];
        var span = payload.AsSpan();
        span[0] = Version;
        span[1] = (byte)kidBytes.Length;
        kidBytes.CopyTo(span[2..]);
        var p = 2 + kidBytes.Length;
        grantId.TryWriteBytes(span[p..], bigEndian: true, out _); p += 16;
        resourceId.TryWriteBytes(span[p..], bigEndian: true, out _); p += 16;
        span[p++] = (byte)purpose;
        span[p++] = (byte)derivativeBytes.Length;
        derivativeBytes.CopyTo(span[p..]); p += derivativeBytes.Length;
        BinaryPrimitives.WriteInt64BigEndian(span[p..], new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)).ToUnixTimeSeconds());
        return payload;
    }

    private static DeliveryTicketClaims? ReadPayload(byte[] payload)
    {
        if (payload.Length < 2 || payload[0] != Version) return null;
        var kidLength = payload[1];
        if (kidLength is < 1 or > HmacKeyRing.MaxKidLength) return null;
        var fixedLength = 1 + 1 + kidLength + 16 + 16 + 1 + 1 + 8;
        if (payload.Length < fixedLength) return null;
        var span = payload.AsSpan();
        var kid = Encoding.ASCII.GetString(span.Slice(2, kidLength));
        var p = 2 + kidLength;
        var grantId = new Guid(span.Slice(p, 16), bigEndian: true); p += 16;
        var resourceId = new Guid(span.Slice(p, 16), bigEndian: true); p += 16;
        var purposeValue = span[p++];
        if (!Enum.IsDefined((DeliveryPurpose)purposeValue)) return null;
        var derivativeLength = span[p++];
        if (derivativeLength > MaxDerivativeLength || payload.Length != fixedLength + derivativeLength) return null;
        var derivative = derivativeLength == 0 ? null : Encoding.ASCII.GetString(span.Slice(p, derivativeLength));
        p += derivativeLength;
        var expires = BinaryPrimitives.ReadInt64BigEndian(span[p..]);
        try
        {
            return new DeliveryTicketClaims(kid, grantId, resourceId, (DeliveryPurpose)purposeValue, derivative,
                DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
