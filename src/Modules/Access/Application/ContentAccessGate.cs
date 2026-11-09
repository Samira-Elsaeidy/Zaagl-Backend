using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Access.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Security;

namespace Zaagl.Modules.Access.Application;

/// <summary>
/// C4 <see cref="IAccessGate"/> and <see cref="IAccessGrants"/> (design/ARC-004-access-gate.md §3-§4, addendum §5).
/// Order: link state → (Redirect stops) → content kind → link key → password grant. Never consumes anything.
/// The link snapshot comes from the cached resolver; negative results are cached too, and an unknown code still does
/// one dummy key compare, so "no such code" and "wrong key" are one timing class and one answer (404).
/// </summary>
public sealed class ContentAccessGate(
    ILinkResolver links,
    LinkKeys keys,
    AccessGrantKeyRing grantKeys,
    IOptions<AccessOptions> options,
    TimeProvider time) : IAccessGate, IAccessGrants
{
    private const byte PayloadVersion = 0x01;

    public Task<AccessGateResult> EvaluateAsync(string code, AccessCredentials credentials, AccessPurpose purpose, CancellationToken cancellationToken) =>
        EvaluateCoreAsync(code, credentials, purpose, checkPassword: true, cancellationToken);

    private async Task<AccessGateResult> EvaluateCoreAsync(
        string code, AccessCredentials credentials, AccessPurpose purpose, bool checkPassword, CancellationToken ct)
    {
        var (result, _) = await EvaluateWithSnapshotAsync(code, credentials, purpose, checkPassword, ct);
        return result;
    }

    private async Task<(AccessGateResult Result, LinkSnapshot? Snapshot)> EvaluateWithSnapshotAsync(
        string code, AccessCredentials credentials, AccessPurpose purpose, bool checkPassword, CancellationToken ct)
    {
        var notFound = new AccessGateResult(AccessGateOutcome.NotFound, null, null, null, false);

        // Step 1: the snapshot (same alias syntax as everywhere; a malformed code is simply not found).
        LinkSnapshot? link = null;
        if (!string.IsNullOrEmpty(code) && code.Length <= SlugRules.MaxLength)
            link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);
        if (link is null)
        {
            keys.DummyCompare(credentials.LinkKey);
            return (notFound, null);
        }

        // Step 2: link state (spec §4.3). IsActive = false is disabled, exactly as the Phase 0 gate (AccessGate.cs).
        var now = time.GetUtcNow().UtcDateTime;
        var state = LinkStateCalculator.Compute(link, now);
        if (state == LinkState.Active && !link.IsActive) state = LinkState.Disabled;
        AccessGateResult Closed(AccessGateOutcome outcome) =>
            new(outcome, link.Id, link.ResourceId, link.TargetKind, link.OwnerId is null, state);
        switch (state)
        {
            case LinkState.Purgeable: return (notFound, link);
            case LinkState.Deleted: return (Closed(AccessGateOutcome.Gone), link);
            case LinkState.Disabled:
                return (Closed(link.DisabledKind is "abuse" or "safety" ? AccessGateOutcome.TakenDown : AccessGateOutcome.Disabled), link);
            case LinkState.Quarantined: return (Closed(AccessGateOutcome.Gone), link);
        }

        // Step 3: the redirect never sees the fragment (addendum §3.3).
        if (purpose == AccessPurpose.Redirect) return (Closed(AccessGateOutcome.Allowed), link);

        // Step 4: content endpoints never serve URL links.
        if (link.TargetKind == LinkTargetKind.Url)
        {
            keys.DummyCompare(credentials.LinkKey);
            return (notFound, link);
        }

        // Step 5: the link key (PD-01). Wrong or missing key answers like an unknown code.
        if (link.LinkKeyHash is { } keyHash)
        {
            if (!keys.Matches(credentials.LinkKey, keyHash)) return (notFound, link);
        }
        else
        {
            keys.DummyCompare(credentials.LinkKey);
        }

        // Step 6: password, through a grant bound to this link, purpose, resource and current password stamp.
        if (checkPassword && link.PasswordHash is { Length: > 0 } passwordHash)
        {
            var grant = Validate(credentials.GrantToken ?? "", link.Id, purpose);
            if (!grant.IsSuccess
                || grant.Value!.ResourceId != link.ResourceId
                || grant.Value.PasswordStamp != PasswordStamp(passwordHash))
                return (Closed(AccessGateOutcome.PasswordRequired), link);
        }

        return (Closed(AccessGateOutcome.Allowed), link);
    }

    // ---- IAccessGrants ---------------------------------------------------------------------------------

    public async Task<Result<AccessGrant>> UnlockAsync(string code, string password, string? linkKey, CancellationToken cancellationToken)
    {
        // ARC-004 §4.1: state and key first (purpose View, password not checked); a wrong key never tests the password.
        var (gate, link) = await EvaluateWithSnapshotAsync(code, new AccessCredentials(linkKey, null), AccessPurpose.View,
            checkPassword: false, cancellationToken);
        if (gate.Outcome != AccessGateOutcome.Allowed) return ToError(gate);
        if (link!.PasswordHash is not { Length: > 0 } passwordHash)
            return Error.Validation("This content has no password.");
        if (string.IsNullOrEmpty(password) || password.Length > 256 || !BCrypt.Net.BCrypt.Verify(password, passwordHash))
            return Error.Of(ErrorCodes.PasswordRequired, "The password is not correct.");

        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(GrantMinutes);
        var token = Issue(new AccessGrantClaims(grantKeys.Ring.SigningKid, link.Id, link.ResourceId,
            [AccessPurpose.View, AccessPurpose.Raw, AccessPurpose.Preview, AccessPurpose.Download],
            now, expires, PasswordStamp(passwordHash)));
        return Result<AccessGrant>.Ok(new AccessGrant(token, expires));
    }

    public Result<AccessGrantClaims> Validate(string token, Guid linkId, AccessPurpose purpose)
    {
        var refused = Error.Of(ErrorCodes.PasswordRequired, "A valid access grant is required.");
        if (HmacKeyRing.Split(token) is not { } parts) return refused;
        var claims = Read(parts.Payload);
        if (claims is null || !grantKeys.Ring.VerifyMac(claims.KeyId, parts.Payload, parts.Mac)) return refused;

        var now = time.GetUtcNow().UtcDateTime;
        if (claims.IssuedAt > now.AddSeconds(60)) return refused;                     // clock skew
        if (now >= claims.ExpiresAt) return refused;
        if (claims.ExpiresAt - claims.IssuedAt > TimeSpan.FromMinutes(GrantMinutes)) return refused;
        if (claims.LinkId != linkId) return refused;
        if (purpose == AccessPurpose.Redirect || !claims.Purposes.Contains(purpose)) return refused;
        return Result<AccessGrantClaims>.Ok(claims);
    }

    /// <summary>Gate outcome → C9 error (ARC-004 §3). The caller maps AccessGateOutcome.Allowed itself.</summary>
    public static Error ToError(AccessGateResult gate) => gate.Outcome switch
    {
        AccessGateOutcome.PasswordRequired => Error.Of(ErrorCodes.PasswordRequired, "This content is protected by a password."),
        AccessGateOutcome.Gone when gate.LinkState == LinkState.Deleted => Error.Of(ErrorCodes.Deleted, "This content was deleted."),
        AccessGateOutcome.Gone => Error.Of(ErrorCodes.Expired, "This content has expired."),
        AccessGateOutcome.Disabled => Error.Of(ErrorCodes.Disabled, "This content has been disabled."),
        AccessGateOutcome.TakenDown => Error.Of(ErrorCodes.TakenDown, "This content was removed under the acceptable use policy."),
        _ => Error.NotFound("Not found."),
    };

    /// <summary>ARC-004 §4.1 step 4: first 4 bytes of SHA-256(UTF-8(password hash)), big-endian.</summary>
    public static int PasswordStamp(string passwordHash) =>
        BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)));

    private int GrantMinutes => Math.Max(1, options.Value.GrantMinutes);

    // ---- wire format (ARC-004 §4.2) ---------------------------------------------------------------------

    internal string Issue(AccessGrantClaims claims)
    {
        var kid = Encoding.ASCII.GetBytes(claims.KeyId);
        var payload = new byte[1 + 1 + kid.Length + 16 + 16 + 1 + 8 + 8 + 4];
        var span = payload.AsSpan();
        span[0] = PayloadVersion;
        span[1] = (byte)kid.Length;
        kid.CopyTo(span[2..]);
        var p = 2 + kid.Length;
        claims.LinkId.TryWriteBytes(span[p..], bigEndian: true, out _); p += 16;
        (claims.ResourceId ?? Guid.Empty).TryWriteBytes(span[p..], bigEndian: true, out _); p += 16;
        byte mask = 0;
        foreach (var purpose in claims.Purposes)
            if (purpose != AccessPurpose.Redirect) mask |= (byte)(1 << (int)purpose);
        span[p++] = mask;
        BinaryPrimitives.WriteInt64BigEndian(span[p..], new DateTimeOffset(DateTime.SpecifyKind(claims.IssuedAt, DateTimeKind.Utc)).ToUnixTimeSeconds()); p += 8;
        BinaryPrimitives.WriteInt64BigEndian(span[p..], new DateTimeOffset(DateTime.SpecifyKind(claims.ExpiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds()); p += 8;
        BinaryPrimitives.WriteInt32BigEndian(span[p..], claims.PasswordStamp);
        return grantKeys.Ring.Sign(payload, claims.KeyId);
    }

    private static AccessGrantClaims? Read(byte[] payload)
    {
        if (payload.Length < 2 || payload[0] != PayloadVersion) return null;
        var kidLength = payload[1];
        if (kidLength is < 1 or > HmacKeyRing.MaxKidLength) return null;
        if (payload.Length != 1 + 1 + kidLength + 16 + 16 + 1 + 8 + 8 + 4) return null;
        var span = payload.AsSpan();
        var kid = Encoding.ASCII.GetString(span.Slice(2, kidLength));
        var p = 2 + kidLength;
        var linkId = new Guid(span.Slice(p, 16), bigEndian: true); p += 16;
        var resourceId = new Guid(span.Slice(p, 16), bigEndian: true); p += 16;
        var mask = span[p++];
        var purposes = Enum.GetValues<AccessPurpose>().Where(x => x != AccessPurpose.Redirect && (mask & (1 << (int)x)) != 0).ToList();
        long issued = BinaryPrimitives.ReadInt64BigEndian(span[p..]); p += 8;
        long expires = BinaryPrimitives.ReadInt64BigEndian(span[p..]); p += 8;
        var stamp = BinaryPrimitives.ReadInt32BigEndian(span[p..]);
        try
        {
            return new AccessGrantClaims(kid, linkId, resourceId == Guid.Empty ? null : resourceId, purposes,
                DateTimeOffset.FromUnixTimeSeconds(issued).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime, stamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
