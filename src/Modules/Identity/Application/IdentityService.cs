using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Identity.Application;

/// <summary>
/// Spec §7.4 accounts, phase 3 of the v9.4 build.
///
/// DEV-MODE SHORTCUT (flagged per instructions): nothing actually sends email yet - that is phase 5's
/// transactional outbox. Confirmation/reset tokens are generated and stored for real (hashed), but
/// "queueing" one here just logs it at Information level AND returns it in the DTO's
/// <c>DevOnlyToken</c> field when running in Development, so this phase's flows can be exercised and
/// tested end to end without SMTP. That field MUST be removed (or the whole DevOnly path gated out) the
/// moment phase 5 wires a real dispatcher - it would otherwise leak reset tokens over the wire.
/// </summary>
public sealed class IdentityService(
    IdentityDbContext db,
    TokenService tokens,
    IPlanProvider plans,
    Zaagl.Modules.Links.Contracts.IAccountDataEraser linkEraser,
    Zaagl.Modules.Links.Contracts.ILinkExportSource linkExport,
    Zaagl.Modules.Analytics.Contracts.IArchiveQueries archiveQueries,
    ILogger<IdentityService> logger)
{
    private const string ApiKeyPrefix = "zgl_";
    private const int MaxApiKeysPerUser = 20;
    private static readonly TimeSpan ConfirmTokenLifetime = TimeSpan.FromDays(3);
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);
    private static readonly string[] AllowedScopes = ["links:read", "links:write", "stats:read"];

    // ---- Registration (spec §7.4: always the same response shape) --------

    public async Task<RegisterOutcome> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (email is null) return new RegisterOutcome(Error.Validation("A valid email address is required."), null);
        if (request.Password is not { Length: >= 8 and <= 128 })
            return new RegisterOutcome(Error.Validation("Password must be between 8 and 128 characters."), null);

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        string? devToken = null;

        if (existing is null)
        {
            var user = new User
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                DisplayName = Truncate(request.DisplayName, 200) ?? email[..email.IndexOf('@')],
                EmailConfirmed = false,
            };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Lost a race with a concurrent register of the same address: treat it the same as
                // "already exists" below rather than surfacing a 500 - the uniform response still holds.
                return await RegisterAsync(request, ct);
            }
            devToken = await QueueConfirmationAsync(user, ct);
        }
        else if (!existing.EmailConfirmed)
        {
            // Restart the registration: replace the password, rotate the stamp, requeue confirmation.
            existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            existing.SecurityStamp = Guid.CreateVersion7();
            await db.SaveChangesAsync(ct);
            devToken = await QueueConfirmationAsync(existing, ct);
        }
        else
        {
            await EmailQueue.QueueAccountEmailAsync(db, existing.Id, EmailKinds.AccountExists, null, null, ct);
            logger.LogInformation("Register: address {Email} already has a confirmed account; queued account-exists notice.", email);
        }

        return new RegisterOutcome(null, devToken);
    }

    private async Task<string> QueueConfirmationAsync(User user, CancellationToken ct)
    {
        var (raw, hash) = NewToken();
        db.EmailTokens.Add(new EmailToken
        {
            UserId = user.Id,
            Kind = EmailTokenKind.ConfirmEmail,
            TokenHash = hash,
            ExpiresAt = DateTime.UtcNow.Add(ConfirmTokenLifetime),
        });
        await EmailQueue.QueueAccountEmailAsync(db, user.Id, EmailKinds.ConfirmEmail, new ConfirmEmailPayload(raw), null, ct);
        logger.LogInformation("Queued confirm-email for user {UserId} (dev-mode devOnlyConfirmToken also returned).", user.Id);
        return raw;
    }

    // ---- Email confirmation ------------------------------------------------

    public async Task<Result<bool>> ConfirmEmailAsync(Guid userId, string token, CancellationToken ct)
    {
        var hash = Hash(token);
        var entry = await db.EmailTokens.FirstOrDefaultAsync(
            t => t.UserId == userId && t.Kind == EmailTokenKind.ConfirmEmail && t.TokenHash == hash, ct);
        if (entry is null || entry.UsedAt is not null || entry.ExpiresAt < DateTime.UtcNow)
            return Error.Validation("This confirmation link is invalid or has expired.");

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.EmailConfirmed = true;
        entry.UsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result<bool>.Ok(true);
    }

    public async Task<string?> ResendConfirmationAsync(string? email, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        var user = normalized is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == normalized && !u.EmailConfirmed, ct);
        return user is null ? null : await QueueConfirmationAsync(user, ct);
    }

    // ---- Password reset -----------------------------------------------------

    public async Task<string?> ForgotPasswordAsync(string? email, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        var user = normalized is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == normalized && u.EmailConfirmed, ct);
        if (user is null) return null;

        var (raw, hash) = NewToken();
        db.EmailTokens.Add(new EmailToken
        {
            UserId = user.Id,
            Kind = EmailTokenKind.PasswordReset,
            TokenHash = hash,
            ExpiresAt = DateTime.UtcNow.Add(ResetTokenLifetime),
        });
        await EmailQueue.QueueAccountEmailAsync(db, user.Id, EmailKinds.PasswordReset, new PasswordResetPayload(raw), null, ct);
        logger.LogInformation("Queued password-reset for user {UserId} (dev-mode devOnlyConfirmToken also returned).", user.Id);
        return raw;
    }

    public async Task<Result<bool>> ResetPasswordAsync(Guid userId, string token, string newPassword, CancellationToken ct)
    {
        if (newPassword is not { Length: >= 8 and <= 128 })
            return Error.Validation("Password must be between 8 and 128 characters.");

        var hash = Hash(token);
        var entry = await db.EmailTokens.FirstOrDefaultAsync(
            t => t.UserId == userId && t.Kind == EmailTokenKind.PasswordReset && t.TokenHash == hash, ct);
        if (entry is null || entry.UsedAt is not null || entry.ExpiresAt < DateTime.UtcNow)
            return Error.Validation("This reset link is invalid or has expired.");

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.SecurityStamp = Guid.CreateVersion7();
        entry.UsedAt = DateTime.UtcNow;

        await RevokeAllRefreshTokensAsync(userId, ct);
        await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct);

        await db.SaveChangesAsync(ct);
        await EmailQueue.QueueAccountEmailAsync(db, userId, EmailKinds.SecurityNotice,
            new SecurityNoticePayload("Your password was just reset and every other session was signed out."), user.Email, ct);
        logger.LogInformation("Password reset for user {UserId}: all refresh tokens and API keys revoked, security-notice queued.", userId);
        return Result<bool>.Ok(true);
    }

    // ---- Login, refresh, logout ---------------------------------------------

    public async Task<LoginOutcome> LoginAsync(LoginRequest request, string ipPartition, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = email is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is not null && await IsLockedAsync(user.Id, ipPartition, ct))
        {
            // Spec §7.4: a pair in its wait period gets the exact same response as wrong credentials.
            return new LoginOutcome(null, null, Error.Unauthorized("Invalid email or password."));
        }

        if (user?.PasswordHash is null || !BCrypt.Net.BCrypt.Verify(request.Password ?? "", user.PasswordHash))
        {
            if (user is not null) await RecordFailureAsync(user.Id, ipPartition, ct);
            else await ProgressiveDelayAsync(0, ct); // unknown address: no account to key the delay on, skip
            return new LoginOutcome(null, null, Error.Unauthorized("Invalid email or password."));
        }

        // Correct password from here on - every further rejection is allowed to be specific, per spec.
        if (!user.EmailConfirmed) return new LoginOutcome(null, null, new Error("email_unconfirmed", "Please confirm your email address before signing in."));
        if (user.DisabledAt is not null || user.DeletedAt is not null) return new LoginOutcome(null, null, new Error("account_disabled", "This account has been disabled."));

        if (user.Role == "Admin")
        {
            if (user.TotpSecret is null)
            {
                var (enrolToken, expires) = tokens.IssueEnrolOnlyToken(user);
                return new LoginOutcome(null, new EnrolTotpIssued(enrolToken, expires), null);
            }
            if (string.IsNullOrEmpty(request.TotpCode) || !Totp.Validate(user.TotpSecret, request.TotpCode, DateTimeOffset.UtcNow))
                return new LoginOutcome(null, null, new Error("totp_required", "A valid authenticator code is required."));
        }

        await ClearFailuresAsync(user.Id, ipPartition, ct);
        return new LoginOutcome(await IssueSessionAsync(user, ct), null, null);
    }

    private async Task<AuthResponse> IssueSessionAsync(User user, CancellationToken ct)
    {
        var (access, accessExpires) = tokens.IssueAccessToken(user);
        var (refreshRaw, refreshHash) = NewToken();
        var refreshExpires = DateTime.UtcNow.AddDays(30);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            FamilyId = Guid.CreateVersion7(),
            TokenHash = refreshHash,
            ExpiresAt = refreshExpires,
        });
        await db.SaveChangesAsync(ct);
        return new AuthResponse(access, accessExpires, refreshRaw, refreshExpires, ToDto(user));
    }

    /// <summary>Spec §7.4 refresh rotation + reuse detection, implemented literally: an atomic
    /// "mark used and return the row" update is the race-free core. If that finds nothing but the token
    /// exists already used, the response depends on how long ago and whether the family is revoked -
    /// within 30s and not revoked is a benign double-submit from the same client (retry with the newer
    /// token); anything else is treated as a stolen token being replayed, and the whole family dies.</summary>
    public async Task<Result<AuthResponse>> RefreshAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var now = DateTime.UtcNow;

        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE identity.refresh_tokens SET used_at = {now}
            WHERE token_hash = {hash} AND used_at IS NULL AND revoked_at IS NULL AND expires_at > {now}", ct);

        if (rows == 0)
        {
            var existing = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (existing is null) return Error.Unauthorized("Invalid refresh token.");

            if (existing.UsedAt is { } usedAt && existing.RevokedAt is null && now - usedAt < TimeSpan.FromSeconds(30))
                return new Error("refresh_raced", "This refresh token was already rotated a moment ago; retry with the token your last refresh returned.");

            // Reused outside the benign race window, or the family is already revoked: treat as theft.
            await db.RefreshTokens.Where(t => t.FamilyId == existing.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            logger.LogWarning("SECURITY: refresh token reuse detected for family {FamilyId}, user {UserId} - family revoked.", existing.FamilyId, existing.UserId);
            return Error.Unauthorized("This refresh token has already been used. For your security, all sessions in this chain have been signed out.");
        }

        var used = await db.RefreshTokens.AsNoTracking().FirstAsync(t => t.TokenHash == hash, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == used.UserId, ct);
        if (user is null || user.DisabledAt is not null || user.DeletedAt is not null) return Error.Unauthorized("Account unavailable.");

        var (access, accessExpires) = tokens.IssueAccessToken(user);
        var (newRaw, newHash) = NewToken();
        var newExpires = DateTime.UtcNow.AddDays(30);
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, FamilyId = used.FamilyId, TokenHash = newHash, ExpiresAt = newExpires });
        await db.SaveChangesAsync(ct);
        return Result<AuthResponse>.Ok(new AuthResponse(access, accessExpires, newRaw, newExpires, ToDto(user)));
    }

    public async Task LogoutAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return;
        await db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
    }

    public async Task LogoutEverywhereAsync(Guid userId, CancellationToken ct)
    {
        await RevokeAllRefreshTokensAsync(userId, ct);
        await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct);
    }

    private async Task RevokeAllRefreshTokensAsync(Guid userId, CancellationToken ct) =>
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);

    // ---- Login protection (spec §7.4) ---------------------------------------

    private async Task<bool> IsLockedAsync(Guid userId, string ipPartition, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddMinutes(-15);
        var count = await db.LoginFailures.CountAsync(f => f.UserId == userId && f.IpPartition == ipPartition && f.CreatedAt >= since, ct);
        return count >= 5;
    }

    private async Task RecordFailureAsync(Guid userId, string ipPartition, CancellationToken ct)
    {
        db.LoginFailures.Add(new LoginFailure { UserId = userId, IpPartition = ipPartition });
        await db.SaveChangesAsync(ct);

        // Progressive per-account delay across all partitions, so an attacker spreading guesses across
        // many IPs still slows down, but nobody (including the real owner) can be locked out entirely.
        var since = DateTime.UtcNow.AddHours(-1);
        var recent = await db.LoginFailures.CountAsync(f => f.UserId == userId && f.CreatedAt >= since, ct);
        await ProgressiveDelayAsync(recent, ct);
    }

    private async Task ClearFailuresAsync(Guid userId, string ipPartition, CancellationToken ct) =>
        await db.LoginFailures.Where(f => f.UserId == userId && f.IpPartition == ipPartition).ExecuteDeleteAsync(ct);

    private static Task ProgressiveDelayAsync(int recentFailures, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Min(10, recentFailures)), ct);

    // ---- TOTP enrollment (admin only) ----------------------------------------

    public async Task<Result<TotpEnrollResponse>> StartTotpEnrollmentAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Error.NotFound("User not found.");
        if (user.TotpSecret is not null) return Error.Conflict("An authenticator is already enrolled.");

        user.TotpSecret = Totp.GenerateSecret();
        await db.SaveChangesAsync(ct);
        return Result<TotpEnrollResponse>.Ok(new TotpEnrollResponse(user.TotpSecret, Totp.OtpAuthUri(user.TotpSecret, user.Email, "Zaagl")));
    }

    public async Task<Result<bool>> ConfirmTotpAsync(Guid userId, string code, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user?.TotpSecret is null) return Error.Validation("No authenticator enrollment in progress.");
        if (!Totp.Validate(user.TotpSecret, code, DateTimeOffset.UtcNow)) return Error.Validation("Incorrect code.");

        user.TotpConfirmedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result<bool>.Ok(true);
    }

    // ---- Unchanged from before this phase -----------------------------------

    public Task<UserDto?> GetUserAsync(Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId)
            .Select(u => new UserDto(u.Id, u.Email, u.DisplayName, u.Role, u.EmailConfirmed))
            .FirstOrDefaultAsync(ct);

    public async Task<Result<AuthResponse>> ExternalLoginAsync(
        string provider, string providerUserId, string? email, string? name, CancellationToken ct)
    {
        provider = provider.ToLowerInvariant();
        var existing = await db.ExternalLogins
            .FirstOrDefaultAsync(l => l.Provider == provider && l.ProviderUserId == providerUserId, ct);
        if (existing is not null)
            return Result<AuthResponse>.Ok(await IssueSessionAsync(await db.Users.FirstAsync(u => u.Id == existing.UserId, ct), ct));

        var normalized = NormalizeEmail(email);
        if (normalized is null) return Error.Validation("The provider did not return a usable email address.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
        {
            // A verified social-login email is treated as confirmed already (spec doesn't cover
            // social login explicitly; the provider already proved ownership of the address).
            user = new User
            {
                Email = normalized,
                DisplayName = Truncate(name, 200) ?? normalized[..normalized.IndexOf('@')],
                EmailConfirmed = true,
            };
            db.Users.Add(user);
        }
        db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = provider, ProviderUserId = providerUserId });
        await db.SaveChangesAsync(ct);
        return Result<AuthResponse>.Ok(await IssueSessionAsync(user, ct));
    }

    public async Task<IReadOnlyList<ApiKeyDto>> ListApiKeysAsync(Guid userId, CancellationToken ct) =>
        await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyDto(k.Id, k.Name, k.Prefix, k.Scopes, k.CreatedAt, k.LastUsedAt))
            .ToListAsync(ct);

    /// <summary>Spec §7.4: API keys are premium-only. This app's plan model is Free/Starter (Billing), not
    /// the spec's Free/Premium - Starter is the paid tier and is treated as the spec's "premium" for this
    /// gate, same mapping used for custom slugs/password/click-limit elsewhere in this codebase.</summary>
    public async Task<Result<CreatedApiKeyDto>> CreateApiKeyAsync(Guid userId, string? name, string[]? requestedScopes, CancellationToken ct)
    {
        var limits = await plans.GetLimitsAsync(userId, ct);
        if (limits.Tier != PlanTier.Starter) return Error.PlanRequired("API keys need the Starter plan.");

        if (await db.ApiKeys.CountAsync(k => k.UserId == userId && k.RevokedAt == null, ct) >= MaxApiKeysPerUser)
            return Error.Conflict($"You can have at most {MaxApiKeysPerUser} active API keys.");

        var scopes = (requestedScopes is { Length: > 0 } ? requestedScopes : AllowedScopes)
            .Where(s => AllowedScopes.Contains(s)).Distinct().ToArray();
        if (scopes.Length == 0) return Error.Validation("At least one valid scope is required.");

        var secret = ApiKeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            UserId = userId,
            Name = Truncate(name, 100) ?? "API key",
            Prefix = secret[..8],
            KeyHash = Hash(secret),
            Scopes = scopes,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return Result<CreatedApiKeyDto>.Ok(new(new(key.Id, key.Name, key.Prefix, key.Scopes, key.CreatedAt, null), secret));
    }

    /// <summary>Spec §7.10: the email webhook adds an address on a hard bounce or complaint.</summary>
    public async Task SuppressEmailAsync(string email, string reason, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null || await db.EmailSuppressions.AnyAsync(s => s.Email == normalized, ct)) return;
        db.EmailSuppressions.Add(new EmailSuppression { Email = normalized, Reason = reason });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RevokeApiKeyAsync(Guid userId, Guid keyId, CancellationToken ct) =>
        await db.ApiKeys.Where(k => k.Id == keyId && k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct) > 0;

    // ---- Account deletion and export (spec §7.14) ---------------------------

    /// <summary>Spec §7.14 export. Scoped simplification: capped at 50,000 links and 10,000 archived
    /// links rather than a true unbounded server-side cursor (see ILinkExportSource's doc comment) -
    /// effectively complete for any real account at this build's scale. Rate limiting (1/hour) is the
    /// caller's responsibility (same pattern as other endpoint-level concerns in this codebase).</summary>
    public async Task<Result<AccountExportDto>> ExportAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.DeletedAt is not null) return Error.NotFound("Account not found.");

        var links = await linkExport.ListAsync(userId, ct);
        var archive = await archiveQueries.ListAsync(userId, 10_000, null, ct);

        return Result<AccountExportDto>.Ok(new(
            new ExportProfileDto(user.Id, user.Email, user.CreatedAt),
            links.Select(l => new ExportLinkDto(l.Code, l.TargetUrl, l.CreatedAt, l.ExpiresAt, l.State, l.ClickCount)).ToList(),
            archive.Items.Select(a => new ExportArchivedLinkDto(a.Code, a.IsCustom, a.TargetUrl, a.CreatedAt, a.ExpiresAt, a.PurgedAt)).ToList()));
    }

    /// <summary>Irreversible. The password check is the confirmation; the last remaining admin cannot
    /// delete itself. See the doc comment on <see cref="User.DeletedAt"/> for the one deliberate deviation
    /// from the literal spec (personal fields cleared immediately rather than kept as a delayed tombstone).</summary>
    public async Task<Result<bool>> DeleteAccountAsync(Guid userId, string password, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Error.NotFound("Account not found.");
        if (user.PasswordHash is null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return Error.Unauthorized("Incorrect password.");

        if (user.Role == "Admin")
        {
            var otherAdmins = await db.Users.CountAsync(u => u.Id != userId && u.Role == "Admin" && u.DeletedAt == null, ct);
            if (otherAdmins == 0) return Error.Conflict("The last remaining admin cannot delete their own account.");
        }

        // Step 0: mark deleted and revoke everything, in one transaction - from here on every other
        // handler's "is this account usable" check (login, refresh, admin list) sees DeletedAt set.
        user.DeletedAt = DateTime.UtcNow;
        await RevokeAllRefreshTokensAsync(userId, ct);
        await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct);

        // Steps 1-2: Links-side soft-delete + archive/stats erasure (cross-module, see IAccountDataEraser).
        await linkEraser.EraseOwnerLinksAsync(userId, ct);

        // Step 3: cancel pending outbox rows, delete idempotency rows.
        await db.EmailOutbox.Where(e => e.UserId == userId && e.Status == EmailOutboxStatuses.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EmailOutboxStatuses.Cancelled), ct);
        await linkEraser.DeleteIdempotencyKeysAsync(userId, ct);

        var confirmedEmail = user.Email;
        await EmailQueue.QueueAccountEmailAsync(db, null, EmailKinds.SecurityNotice,
            new SecurityNoticePayload("Your Zaagl account was just deleted, as you requested."), confirmedEmail, ct);

        // Step 4: clear personal data (see the deviation noted on User.DeletedAt).
        user.Email = $"deleted-{userId:N}@deleted.zaagl.invalid";
        user.PasswordHash = null;
        user.DisplayName = "Deleted user";
        user.TotpSecret = null;
        user.TotpConfirmedAt = null;
        user.Role = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Account {UserId} deleted (password-confirmed).", userId);
        return Result<bool>.Ok(true);
    }

    // ---- Admin (spec §7.9's /api/admin/users*) ------------------------------

    public async Task<PagedResult<AdminUserDto>> AdminListUsersAsync(string? email, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var q = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(email))
            q = q.Where(u => EF.Functions.ILike(u.Email, $"%{email.Trim()}%"));

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new AdminUserDto(u.Id, u.Email, u.EmailConfirmed, u.Role, u.CreatedAt, u.DisabledAt, u.DisabledWithLinks))
            .ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    /// <summary>Links-side disabling (if <paramref name="disableLinks"/>) is the caller's responsibility -
    /// this only touches Identity's own data, per this solution's module boundary.</summary>
    public async Task<Result<bool>> AdminDisableUserAsync(Guid adminId, Guid userId, string reason, bool disableLinks, CancellationToken ct)
    {
        if (adminId == userId) return Error.Conflict("You cannot disable your own account.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Error.NotFound("User not found.");
        if (user.DisabledAt is not null) return Error.Conflict("This user is already disabled.");

        user.DisabledAt = DateTime.UtcNow;
        user.DisabledReason = Truncate(reason, 500);
        user.DisabledWithLinks = disableLinks;
        await RevokeAllRefreshTokensAsync(userId, ct);
        await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        return Result<bool>.Ok(true);
    }

    /// <summary>Returns whether the user's links should be restored (<paramref name="restoreLinks"/> if
    /// given, else whatever the matching disable used) so the caller can act on Links.</summary>
    public async Task<Result<bool>> AdminEnableUserAsync(Guid userId, bool? restoreLinks, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Error.NotFound("User not found.");
        if (user.DisabledAt is null) return Error.Conflict("This user is not disabled.");

        var shouldRestoreLinks = restoreLinks ?? user.DisabledWithLinks;
        user.DisabledAt = null;
        user.DisabledReason = null;
        user.DisabledWithLinks = false;
        await db.SaveChangesAsync(ct);
        return Result<bool>.Ok(shouldRestoreLinks);
    }

    /// <summary>Returns the owning user id and the key's scopes for a raw API key, or null when invalid or revoked.</summary>
    public async Task<AuthenticatedPrincipal?> ValidateApiKeyAsync(string rawKey, CancellationToken ct)
    {
        if (!rawKey.StartsWith(ApiKeyPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(rawKey);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
        if (key is null) return null;

        if (key.LastUsedAt is null || key.LastUsedAt < DateTime.UtcNow.AddMinutes(-5))
            await db.ApiKeys.Where(k => k.Id == key.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow), ct);
        return new AuthenticatedPrincipal(key.UserId, key.Scopes);
    }

    // ---- helpers --------------------------------------------------------------

    private static (string Raw, string Hash) NewToken()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.Role, user.EmailConfirmed);

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return null;
        var trimmed = email.Trim();
        return MailAddress.TryCreate(trimmed, out var parsed) && parsed.Address == trimmed
            ? trimmed.ToLowerInvariant()
            : null;
    }

    private static string? Truncate(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record RegisterOutcome(Error? Error, string? DevOnlyConfirmToken);

public sealed record EnrolTotpIssued(string EnrolToken, DateTime ExpiresAt);

/// <summary>Exactly one of the three is set: a full session, an enrol-only token (admin without TOTP
/// enrolled yet), or an error.</summary>
public sealed record LoginOutcome(AuthResponse? Session, EnrolTotpIssued? Enrol, Error? Error);
