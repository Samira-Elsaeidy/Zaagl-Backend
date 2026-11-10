using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Identity.Application;

/// <summary>Configuration section <c>DeviceAuth</c> (technical defaults, not product rules; ARC-014 §2).</summary>
public sealed class DeviceAuthOptions
{
    public const string Section = "DeviceAuth";

    /// <summary>The /activate page of the frontend (<see cref="DeviceFlowWire.VerificationUriConfigKey"/>).</summary>
    public string VerificationUri { get; set; } = DeviceFlowWire.DefaultVerificationUri;
    public int ExpiresInSeconds { get; set; } = DeviceFlowWire.DefaultExpiresInSeconds;
    public int IntervalSeconds { get; set; } = DeviceFlowWire.DefaultIntervalSeconds;
    /// <summary>Wrong user codes a user may enter within <see cref="LockoutWindowMinutes"/> before the lockout.</summary>
    public int LockoutMaxFailures { get; set; } = 10;
    public int LockoutWindowMinutes { get; set; } = 10;
    public int LockoutMinutes { get; set; } = 15;
    /// <summary>Sliding lifetime of a device session's refresh token (interim answer: 30 days, no absolute cap).</summary>
    public int SessionSlidingDays { get; set; } = 30;
}

/// <summary>
/// BE-024 / ARC-014: RFC 8628 device authorization for the CLI and the desktop app, and the device sessions it
/// creates. Device and user codes are stored hashed; approve and redeem are conditional updates with one winner.
/// </summary>
public sealed class DeviceFlowService(
    IdentityDbContext db,
    TokenService tokens,
    IOptions<DeviceAuthOptions> options,
    TimeProvider time,
    IMemoryCache cache)
{
    private readonly DeviceAuthOptions _o = options.Value;

    /// <summary>Slack for clients whose timer fires marginally early.</summary>
    private static readonly TimeSpan PollTolerance = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SweepAfter = TimeSpan.FromMinutes(10);
    private const int MaxNameLength = 64, MaxOsLength = 32;

    private static readonly Error LockedError = Error.TooManyRequests("Too many wrong codes. Try again later.");

    public static bool IsNativeClient(string? clientId) => clientId is NativeClientIds.Cli or NativeClientIds.Desktop;

    // ---- POST /api/auth/device/code -------------------------------------------------------------

    public async Task<(DeviceCodeResponse? Response, DeviceTokenError? Error)> StartAsync(DeviceCodeRequest? request, CancellationToken ct)
    {
        if (request is null) return (null, Err(DeviceFlowWire.InvalidRequest, "The request body is missing or malformed."));
        if (!IsNativeClient(request.ClientId)) return (null, Err(DeviceFlowWire.InvalidClient, "Unknown client_id."));
        if (request.DeviceName is { Length: > MaxNameLength } || request.Os is { Length: > MaxOsLength })
            return (null, Err(DeviceFlowWire.InvalidRequest, $"device_name is at most {MaxNameLength} and os at most {MaxOsLength} characters."));

        var now = Now();
        await db.DeviceAuthorizations.Where(a => a.ExpiresAt < now - SweepAfter).ExecuteDeleteAsync(ct);

        var deviceCode = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddSeconds(_o.ExpiresInSeconds);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var userCode = NewUserCode();
            db.DeviceAuthorizations.Add(new DeviceAuthorization
            {
                DeviceCodeHash = Sha256(deviceCode),
                UserCodeHash = Sha256(userCode),
                ClientId = request.ClientId,
                DeviceName = Sanitize(request.DeviceName, MaxNameLength),
                Os = Sanitize(request.Os, MaxOsLength),
                Status = DeviceAuthorizationStatus.Pending,
                CreatedAt = now,
                ExpiresAt = expires,
                PollIntervalSeconds = _o.IntervalSeconds,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // a user-code collision: pick another
                continue;
            }
            var display = $"{userCode[..4]}-{userCode[4..]}";
            var uri = _o.VerificationUri;
            var complete = $"{uri}{(uri.Contains('?') ? '&' : '?')}code={display}";
            return (new DeviceCodeResponse(deviceCode, display, uri, complete, _o.ExpiresInSeconds, _o.IntervalSeconds), null);
        }
        throw new InvalidOperationException("Could not allocate a unique user code.");
    }

    // ---- POST /api/auth/device/token ------------------------------------------------------------

    public async Task<(DeviceAuthResponse? Response, DeviceTokenError? Error)> RedeemAsync(DeviceTokenRequest? request, CancellationToken ct)
    {
        if (request is null) return (null, Err(DeviceFlowWire.InvalidRequest, "The request body is missing or malformed."));
        if (request.GrantType != DeviceFlowWire.GrantType) return (null, Err(DeviceFlowWire.UnsupportedGrantType, "Unsupported grant_type."));
        if (!IsNativeClient(request.ClientId)) return (null, Err(DeviceFlowWire.InvalidClient, "Unknown client_id."));
        if (string.IsNullOrEmpty(request.DeviceCode) || request.DeviceCode.Length > 128)
            return (null, Err(DeviceFlowWire.InvalidGrant, "Unknown device_code."));

        var hash = Sha256(request.DeviceCode);
        var row = await db.DeviceAuthorizations.AsNoTracking().FirstOrDefaultAsync(a => a.DeviceCodeHash == hash, ct);
        if (row is null) return (null, Err(DeviceFlowWire.InvalidGrant, "Unknown device_code."));
        if (row.ClientId != request.ClientId) return (null, Err(DeviceFlowWire.InvalidClient, "client_id does not match the device_code."));
        if (row.Status == DeviceAuthorizationStatus.Redeemed) return (null, Err(DeviceFlowWire.InvalidGrant, "This device_code was already used."));

        var now = Now();
        if (row.ExpiresAt <= now) return (null, Err(DeviceFlowWire.ExpiredToken, "The device_code has expired."));

        if (row.Status == DeviceAuthorizationStatus.Denied)
        {
            await db.DeviceAuthorizations.Where(a => a.Id == row.Id).ExecuteDeleteAsync(ct);
            return (null, Err(DeviceFlowWire.AccessDenied, "The user denied the request."));
        }

        // RFC 8628 §3.5: polling faster than the interval gets slow_down and a longer interval.
        if (row.LastPolledAt is { } last && now - last < TimeSpan.FromSeconds(row.PollIntervalSeconds) - PollTolerance)
        {
            var next = row.PollIntervalSeconds + DeviceFlowWire.SlowDownIncrementSeconds;
            await db.DeviceAuthorizations.Where(a => a.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.PollIntervalSeconds, next).SetProperty(a => a.LastPolledAt, now), ct);
            return (null, Err(DeviceFlowWire.SlowDown, "Polling too fast; increase the interval by 5 seconds."));
        }
        await db.DeviceAuthorizations.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastPolledAt, now), ct);

        if (row.Status == DeviceAuthorizationStatus.Pending)
            return (null, Err(DeviceFlowWire.AuthorizationPending, "The user has not approved the request yet."));

        // Approved: single use, exactly one caller wins the conditional update.
        var won = await db.DeviceAuthorizations
            .Where(a => a.Id == row.Id && a.Status == DeviceAuthorizationStatus.Approved && a.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, DeviceAuthorizationStatus.Redeemed), ct);
        if (won != 1) return (null, Err(DeviceFlowWire.InvalidGrant, "This device_code was already used."));

        var user = row.UserId is { } uid ? await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct) : null;
        if (user is null || user.DisabledAt is not null || user.DeletedAt is not null)
            return (null, Err(DeviceFlowWire.AccessDenied, "The account is unavailable."));

        var familyId = Guid.CreateVersion7();
        var (access, accessExpires) = tokens.IssueDeviceAccessToken(user, row.ClientId, familyId);
        var (refreshRaw, refreshHash) = NewRefreshToken();
        var refreshExpires = now.AddDays(_o.SessionSlidingDays);
        db.DeviceSessions.Add(new DeviceSession
        {
            FamilyId = familyId,
            UserId = user.Id,
            ClientId = row.ClientId,
            DeviceName = row.DeviceName,
            Os = row.Os,
            CreatedAt = now,
        });
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = refreshExpires,
        });
        await db.SaveChangesAsync(ct);
        return (new DeviceAuthResponse(access, accessExpires, refreshRaw, refreshExpires, DeviceUser(user), familyId), null);
    }

    /// <summary>The user shown to a device never carries the Admin role (ARC-014 §2.4).</summary>
    public static UserDto DeviceUser(User u) => new(u.Id, u.Email, u.DisplayName, null, u.EmailConfirmed);

    // ---- GET /api/auth/device/activation and POST /api/auth/device/activate -----------------------

    /// <summary>The pending request, or NotFound (unknown, expired, used, denied: no distinction), or
    /// TooManyRequests while the user is locked out.</summary>
    public async Task<Result<DeviceActivationInfo>> GetActivationAsync(Guid userId, string? userCode, CancellationToken ct)
    {
        if (await IsLockedAsync(userId, ct)) return LockedError;
        var row = await FindPendingAsync(userCode, ct);
        if (row is null) return await FailAsync<DeviceActivationInfo>(userId, ct);
        return Result<DeviceActivationInfo>.Ok(new DeviceActivationInfo(
            Display(userCode!), row.ClientId, row.DeviceName, row.Os, row.CreatedAt, row.ExpiresAt));
    }

    public async Task<Result<bool>> ActivateAsync(Guid userId, string? userCode, bool approve, CancellationToken ct)
    {
        if (await IsLockedAsync(userId, ct)) return LockedError;
        var row = await FindPendingAsync(userCode, ct);
        if (row is null) return await FailAsync<bool>(userId, ct);

        var now = Now();
        var status = approve ? DeviceAuthorizationStatus.Approved : DeviceAuthorizationStatus.Denied;
        var changed = await db.DeviceAuthorizations
            .Where(a => a.Id == row.Id && a.Status == DeviceAuthorizationStatus.Pending && a.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status).SetProperty(a => a.UserId, userId), ct);
        return changed == 1 ? Result<bool>.Ok(true) : Error.NotFound("Unknown or expired code.");
    }

    private async Task<DeviceAuthorization?> FindPendingAsync(string? userCode, CancellationToken ct)
    {
        var normalized = Normalize(userCode);
        if (normalized is null) return null;
        var hash = Sha256(normalized);
        var now = Now();
        return await db.DeviceAuthorizations.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserCodeHash == hash && a.Status == DeviceAuthorizationStatus.Pending && a.ExpiresAt > now, ct);
    }

    private async Task<bool> IsLockedAsync(Guid userId, CancellationToken ct)
    {
        var now = Now();
        return await db.DeviceActivationFailures.AnyAsync(f => f.UserId == userId && f.LockedUntil > now, ct);
    }

    private async Task<Result<T>> FailAsync<T>(Guid userId, CancellationToken ct)
    {
        var now = Now();
        var since = now.AddMinutes(-_o.LockoutWindowMinutes);
        await db.DeviceActivationFailures.Where(f => f.CreatedAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);
        var recent = await db.DeviceActivationFailures.CountAsync(f => f.UserId == userId && f.CreatedAt > since, ct);
        db.DeviceActivationFailures.Add(new DeviceActivationFailure
        {
            UserId = userId,
            CreatedAt = now,
            LockedUntil = recent + 1 >= _o.LockoutMaxFailures ? now.AddMinutes(_o.LockoutMinutes) : null,
        });
        await db.SaveChangesAsync(ct);
        return Error.NotFound("Unknown or expired code.");
    }

    // ---- Device sessions ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeviceSessionDto>> ListSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = Now();
        // Live while not revoked and a refresh token of the family is still usable (30-day sliding).
        var rows = await db.DeviceSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null
                && db.RefreshTokens.Any(t => t.FamilyId == s.FamilyId && t.RevokedAt == null && t.ExpiresAt > now))
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(s => new DeviceSessionDto(s.FamilyId, s.ClientId, s.DeviceName, s.Os, s.CreatedAt, s.LastUsedAt)).ToList();
    }

    /// <summary>Revokes one of the caller's device sessions: its refresh family at once and its access tokens within
    /// 60 s. False when it is unknown or not theirs.</summary>
    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = Now();
        var owned = await db.DeviceSessions.AnyAsync(s => s.FamilyId == sessionId && s.UserId == userId, ct);
        if (!owned) return false;
        await db.DeviceSessions.Where(s => s.FamilyId == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await db.RefreshTokens.Where(t => t.FamilyId == sessionId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        cache.Remove(DeviceSessionValidator.CacheKey(sessionId));
        return true;
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private static DeviceTokenError Err(string code, string description) => new(code, description);

    /// <summary>Upper-case, hyphen and spaces removed; null unless exactly 8 characters of the user-code alphabet.</summary>
    public static string? Normalize(string? userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode) || userCode.Length > 32) return null;
        var s = new string(userCode.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        return s.Length == DeviceFlowWire.UserCodeLength && s.All(c => DeviceFlowWire.UserCodeAlphabet.Contains(c)) ? s : null;
    }

    private static string Display(string userCode)
    {
        var n = Normalize(userCode)!;
        return $"{n[..4]}-{n[4..]}";
    }

    private static string NewUserCode()
    {
        var chars = new char[DeviceFlowWire.UserCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = DeviceFlowWire.UserCodeAlphabet[RandomNumberGenerator.GetInt32(DeviceFlowWire.UserCodeAlphabet.Length)];
        return new string(chars);
    }

    /// <summary>Strips control, format (bidi) and line/paragraph separator characters, trims, truncates.</summary>
    private static string? Sanitize(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsControl(ch) || cat is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        if (s.Length > max) s = s[..max];
        return s.Length == 0 ? null : s;
    }

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Same hashing as <c>IdentityService</c> (upper-case hex SHA-256) so a refresh finds the row.</summary>
    private static (string Raw, string Hash) NewRefreshToken()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (raw, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))));
    }
}
