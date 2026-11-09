using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Application;

/// <summary>
/// C1 (ARC-002 §5): content links are ordinary links whose target is a Resources row. They reuse the Phase 0
/// engine (leases, keyed scramble, reserved words, alias lock, blocked aliases, denylist, restore freeze,
/// idempotency) through <see cref="AllocateAndInsertAsync{T}"/>, and never run the URL-only steps: no URL
/// normalisation, no Web Risk lookup, no destination blocklist, no routing rules, no fallback URL, no Open Graph,
/// no monthly link cap and no plan check (content plan rules are the caller's, through C5).
/// </summary>
public sealed partial class LinkService : ILinkIssuer
{
    public async Task<Result<ContentLinkIssued>> CreateContentLinkAsync(ContentLinkRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = time.GetUtcNow().UtcDateTime;

        // ---- 1. Validate (the caller already clamped the expiry to the plan, addendum §9.1) ----
        if (request.Kind is not (LinkTargetKind.Snippet or LinkTargetKind.File or LinkTargetKind.Media))
            return Error.Validation("A content link needs a content kind (snippet, file or media).");
        if (request.ResourceId == Guid.Empty)
            return Error.Validation("A content link needs a resource id.");
        if ((request.OwnerId is null) == (request.GuestCreatorKey is null))
            return Error.Validation("Exactly one of owner and guest creator key must be set.");
        var expiresAt = DateTime.SpecifyKind(request.ExpiresAt, DateTimeKind.Utc);
        if (request.ExpiresAt == default || expiresAt <= now)
            return Error.Validation("Every content link needs an expiry in the future.");
        if (request.Password is { } pw && (pw.Length < 4 || pw.Length > 128))
            return Error.Validation("The password must be 4-128 characters.");

        var customSlug = string.IsNullOrWhiteSpace(request.CustomSlug) ? null : request.CustomSlug.Trim();
        if (customSlug is not null)
        {
            // The same alias rules as LinkInputValidator (spec §4.5).
            if (!SlugRules.IsValidFormat(customSlug))
                return Error.Validation($"Slug must be {SlugRules.MinCustomLength}-{SlugRules.MaxLength} characters: letters, digits, '-' or '_', starting with a letter or digit.");
            if (SlugRules.IsReserved(customSlug) || codes.IsGeneratorReachable(customSlug))
                return Error.Validation("This slug is reserved.");
        }

        // ---- 2. Idempotency: same scopes as URL creation, key = the resource id ----
        var scope = request.OwnerId is { } ownerId ? $"u:{ownerId}" : $"g:{Convert.ToHexString(request.GuestCreatorKey!)}";
        var idempotencyKey = $"content:{request.ResourceId:N}";
        var requestHash = ContentRequestHash(request, customSlug, expiresAt);

        // Replay: the plain link key is never stored (addendum §5.1.2, §16.7), so a replay returns the stored link
        // data with LinkKey = null. What a retried share should return instead is ARC-002 Q1 (not decided); the
        // caller can rotate with IContentLinkCommands.IssueNewLinkKeyAsync once that is built.
        var existing = await db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Scope == scope && k.Key == idempotencyKey, ct);
        if (existing is { Status: "done" })
        {
            return existing.RequestHash.AsSpan().SequenceEqual(requestHash)
                ? Result<ContentLinkIssued>.Ok(System.Text.Json.JsonSerializer.Deserialize<ContentLinkIssued>(existing.ResponseBody!)!)
                : Error.Of(ErrorCodes.IdempotencyMismatch, "idempotency-mismatch: this resource already has a link issued with different settings.");
        }

        if (!creationGate.CanCreate)
            return Error.ServiceUnavailable($"Link creation is temporarily unavailable ({creationGate.State}). Try again shortly.");

        // ---- 3. Link key (PD-01, ARC-004 §2): 16 CSPRNG bytes, base64url (22 chars), only SHA-256 stored ----
        string? plainKey = null;
        byte[]? keyHash = null;
        if (request.RequireLinkKey)
        {
            var raw = RandomNumberGenerator.GetBytes(16);
            plainKey = Base64Url.EncodeToString(raw);
            keyHash = SHA256.HashData(raw);
        }
        var passwordHash = request.Password is { Length: > 0 } password ? BCrypt.Net.BCrypt.HashPassword(password) : null;
        var purgeAfter = request.OwnerId is null ? expiresAt : expiresAt.AddDays(options.Value.QuarantineDays);
        var baseUrl = options.Value.BaseUrl.TrimEnd('/');

        ContentLinkIssued Issued(Link link, string? key) =>
            new(link.Id, link.Slug, $"{baseUrl}/{link.Slug}", expiresAt, purgeAfter, key);

        return await AllocateAndInsertAsync(new AllocationRequest<ContentLinkIssued>(
            CustomSlug: customSlug,
            OwnerId: request.OwnerId,
            Now: () => time.GetUtcNow().UtcDateTime,
            InsideTransactionAsync: async c =>
            {
                var claimed = await TryClaimIdempotencyKeyAsync<ContentLinkIssued>(scope, idempotencyKey, requestHash, c);
                if (claimed.AlreadyDone is { } stored)
                    return stored.Mismatch
                        ? Error.Of(ErrorCodes.IdempotencyMismatch, "idempotency-mismatch: this resource already has a link issued with different settings.")
                        : Result<ContentLinkIssued>.Ok(stored.Dto!);
                return null;
            },
            Configure: link =>
            {
                // ARC-002 §5.5: no destination, no host, no fallback, no click limit, no public statistics.
                link.GuestCreatorKey = request.GuestCreatorKey;
                link.TargetKind = request.Kind;
                link.ResourceId = request.ResourceId;
                link.DestinationUrl = null;
                link.TargetHost = null;
                link.Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim()[..Math.Min(request.Title.Trim().Length, 200)];
                link.PublicStats = false;
                link.IsActive = true;
                link.Policy = new AccessPolicy
                {
                    LinkId = link.Id,
                    ExpiresAt = expiresAt,
                    PasswordHash = passwordHash,
                    LinkKeyHash = keyHash,
                    MaxClicks = null,
                    FallbackUrl = null,
                };
                link.PurgeAfter = purgeAfter;
            },
            // The stored replay never contains the plain key.
            BeforeCommitAsync: (link, c) => CompleteIdempotencyKeyAsync(scope, idempotencyKey, Issued(link, null), c),
            Respond: link => Issued(link, plainKey)), ct);
    }

    /// <summary>ARC-002 §5.2: SHA-256 of <c>kind \n resourceId \n customSlug \n expiresAt(ISO, ms) \n requireLinkKey \n hasPassword</c>.</summary>
    private static byte[] ContentRequestHash(ContentLinkRequest r, string? customSlug, DateTime expiresAtUtc)
    {
        var material = string.Join('\n',
            ((short)r.Kind).ToString(CultureInfo.InvariantCulture),
            r.ResourceId.ToString("N"),
            customSlug ?? "",
            expiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            r.RequireLinkKey ? "1" : "0",
            r.Password is { Length: > 0 } ? "1" : "0");
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }
}
