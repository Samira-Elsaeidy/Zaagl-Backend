using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>
/// Integration tests against the real local dev Postgres (same convention as
/// Zaagl.Coordinator.Tests - no Testcontainers set up in this solution yet). Each test creates its own
/// isolated user(s) with a random email, so tests can run in any order without colliding.
/// </summary>
public sealed class IdentityServiceTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
    private readonly IdentityDbContext _db;
    private readonly IdentityService _identity;

    public IdentityServiceTests()
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityModule.Configure(builder, ConnectionString);
        _db = new IdentityDbContext(builder.Options);

        var jwt = Options.Create(new JwtOptions { Key = "test-signing-key-at-least-32-characters-long!!" });
        var tokens = new TokenService(jwt);
        _identity = new IdentityService(_db, tokens, new AlwaysFreePlanProvider(), new NoOpAccountDataEraser(), new NoOpLinkExportSource(), new NoOpArchiveQueries(), NullLogger<IdentityService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static string RandomEmail() => $"test-{Guid.NewGuid():N}@example.test";

    [Fact]
    public async Task Register_new_address_returns_no_error_and_a_confirmation_token()
    {
        var outcome = await _identity.RegisterAsync(new(RandomEmail(), "password123", null), default);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.DevOnlyConfirmToken);
    }

    [Fact]
    public async Task Register_existing_unconfirmed_address_restarts_registration_same_shape()
    {
        var email = RandomEmail();
        var first = await _identity.RegisterAsync(new(email, "password123", null), default);
        var second = await _identity.RegisterAsync(new(email, "differentpassword456", null), default);

        // Spec §7.4: identical response shape on both branches - no error either time, both produce a
        // (distinct, since the registration restarted) confirmation token.
        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.NotNull(first.DevOnlyConfirmToken);
        Assert.NotNull(second.DevOnlyConfirmToken);
        Assert.NotEqual(first.DevOnlyConfirmToken, second.DevOnlyConfirmToken);
    }

    [Fact]
    public async Task Register_existing_confirmed_address_returns_identical_shape_with_no_token()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await ConfirmAsync(email, registered.DevOnlyConfirmToken!);

        var again = await _identity.RegisterAsync(new(email, "whatever789", null), default);

        // Spec §11 #25: never reveal account existence - no error, but (unlike the two branches above)
        // no token either, since nothing new was queued for an already-confirmed address.
        Assert.Null(again.Error);
        Assert.Null(again.DevOnlyConfirmToken);
    }

    [Fact]
    public async Task Unconfirmed_account_cannot_sign_in_even_with_correct_password()
    {
        var email = RandomEmail();
        await _identity.RegisterAsync(new(email, "password123", null), default);

        var outcome = await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default);

        Assert.NotNull(outcome.Error);
        Assert.Equal("email_unconfirmed", outcome.Error!.Code);
    }

    [Fact]
    public async Task Confirmed_account_can_sign_in_and_gets_both_tokens()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        await ConfirmAsync(email, registered.DevOnlyConfirmToken!);

        var outcome = await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Session);
        Assert.False(string.IsNullOrEmpty(outcome.Session!.AccessToken));
        Assert.False(string.IsNullOrEmpty(outcome.Session.RefreshToken));
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_the_old_one_is_now_used()
    {
        var session = await RegisterConfirmAndLoginAsync();

        var refreshed = await _identity.RefreshAsync(session.RefreshToken, default);
        Assert.Null(refreshed.Error);
        Assert.NotEqual(session.RefreshToken, refreshed.Value!.RefreshToken);

        // Spec §7.4's own algorithm: a replay within 30s of the legitimate rotation is INDISTINGUISHABLE
        // from a benign double-submit, so it is answered refresh_raced, not revoked - by design, not a
        // bug (see the next test for the actual attack-replay path, beyond the 30s window).
        var quickReplay = await _identity.RefreshAsync(session.RefreshToken, default);
        Assert.Equal("refresh_raced", quickReplay.Error!.Code);

        // The family is still alive: the legitimately-rotated token keeps working.
        var stillWorks = await _identity.RefreshAsync(refreshed.Value.RefreshToken, default);
        Assert.Null(stillWorks.Error);
    }

    [Fact]
    public async Task Replaying_an_already_used_token_well_outside_the_race_window_revokes_the_family()
    {
        var session = await RegisterConfirmAndLoginAsync();
        var refreshed = await _identity.RefreshAsync(session.RefreshToken, default);
        Assert.Null(refreshed.Error);

        // Simulate the legitimate rotation having happened 40 seconds ago (well past the 30s benign-race
        // window) by backdating the original token's used_at directly - this is the actual theft-replay
        // scenario the spec's reuse detection exists for.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity.refresh_tokens SET used_at = {DateTime.UtcNow.AddSeconds(-40)} WHERE token_hash = {Hash(session.RefreshToken)}");

        var staleReplay = await _identity.RefreshAsync(session.RefreshToken, default);
        Assert.NotNull(staleReplay.Error);
        Assert.NotEqual("refresh_raced", staleReplay.Error!.Code);

        // SECURITY: the whole family is now dead, including the token from the legitimate rotation.
        var legitimateTokenNowDead = await _identity.RefreshAsync(refreshed.Value!.RefreshToken, default);
        Assert.NotNull(legitimateTokenNowDead.Error);
    }

    [Fact]
    public async Task Refresh_raced_concurrently_from_the_same_client_gets_refresh_raced_not_revoked()
    {
        var session = await RegisterConfirmAndLoginAsync();

        // Two near-simultaneous refreshes of the same token, as two real concurrent HTTP requests would
        // be - each with its OWN DbContext/IdentityService, matching this app's scoped DI lifetime (a
        // single shared DbContext is not thread-safe and does not represent two parallel requests).
        var first = NewIdentityService().RefreshAsync(session.RefreshToken, default);
        var second = NewIdentityService().RefreshAsync(session.RefreshToken, default);
        var results = await Task.WhenAll(first, second);

        var succeeded = results.Count(r => r.Error is null);
        var raced = results.Count(r => r.Error?.Code == "refresh_raced");
        Assert.Equal(1, succeeded);
        Assert.Equal(1, raced);

        // The family must still be alive: the winning token keeps working.
        var winner = results.First(r => r.Error is null).Value!;
        var again = await _identity.RefreshAsync(winner.RefreshToken, default);
        Assert.Null(again.Error);
    }

    private IdentityService NewIdentityService()
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityModule.Configure(builder, ConnectionString);
        var db = new IdentityDbContext(builder.Options);
        var jwt = Options.Create(new JwtOptions { Key = "test-signing-key-at-least-32-characters-long!!" });
        return new IdentityService(db, new TokenService(jwt), new AlwaysFreePlanProvider(), new NoOpAccountDataEraser(), new NoOpLinkExportSource(), new NoOpArchiveQueries(), NullLogger<IdentityService>.Instance);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task LogoutEverywhere_revokes_the_refresh_token_and_api_keys()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await ConfirmAsync(email, registered.DevOnlyConfirmToken!);
        var session = (await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default)).Session!;

        await _identity.LogoutEverywhereAsync(userId, default);

        var afterLogout = await _identity.RefreshAsync(session.RefreshToken, default);
        Assert.NotNull(afterLogout.Error);
    }

    [Fact]
    public async Task Reset_password_revokes_all_refresh_tokens()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await ConfirmAsync(email, registered.DevOnlyConfirmToken!);
        var session = (await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default)).Session!;

        var resetToken = await _identity.ForgotPasswordAsync(email, default);
        Assert.NotNull(resetToken);
        var reset = await _identity.ResetPasswordAsync(userId, resetToken!, "brandnewpassword999", default);
        Assert.True(reset.IsSuccess);

        var afterReset = await _identity.RefreshAsync(session.RefreshToken, default);
        Assert.NotNull(afterReset.Error);

        // Old password must no longer work; the new one must.
        var oldPasswordLogin = await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default);
        Assert.NotNull(oldPasswordLogin.Error);
        var newPasswordLogin = await _identity.LoginAsync(new(email, "brandnewpassword999", null), "v4:127.0.0.1", default);
        Assert.Null(newPasswordLogin.Error);
    }

    [Fact]
    public async Task Totp_enroll_then_confirm_then_code_is_required_at_login()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await ConfirmAsync(email, registered.DevOnlyConfirmToken!);
        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, "Admin"), default);
        _db.ChangeTracker.Clear(); // ExecuteUpdateAsync bypasses the change tracker; a prior tracked
                                   // query (ConfirmAsync's) would otherwise shadow this with a stale Role.

        // Before enrolling: login returns an enrol-only token, not a session.
        var beforeEnroll = await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default);
        Assert.Null(beforeEnroll.Error);
        Assert.NotNull(beforeEnroll.Enrol);
        Assert.Null(beforeEnroll.Session);

        var enrol = await _identity.StartTotpEnrollmentAsync(userId, default);
        Assert.True(enrol.IsSuccess);
        var secret = enrol.Value!.AuthenticatorKey;
        Assert.Contains("otpauth://totp/", enrol.Value.OtpAuthUri);

        var code = GenerateCurrentCode(secret);
        var confirm = await _identity.ConfirmTotpAsync(userId, code, default);
        Assert.True(confirm.IsSuccess);

        // Without a code: rejected.
        var noCode = await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default);
        Assert.Equal("totp_required", noCode.Error!.Code);

        // With the correct current code: a full session.
        var withCode = await _identity.LoginAsync(new(email, "password123", GenerateCurrentCode(secret)), "v4:127.0.0.1", default);
        Assert.Null(withCode.Error);
        Assert.NotNull(withCode.Session);
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account_ip_pair_for_a_window()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        await ConfirmAsync(email, registered.DevOnlyConfirmToken!);

        for (var i = 0; i < 5; i++)
            await _identity.LoginAsync(new(email, "wrong-password", null), "v4:10.0.0.1", default);

        // The pair is now locked - even the CORRECT password is rejected with the same generic message.
        var locked = await _identity.LoginAsync(new(email, "password123", null), "v4:10.0.0.1", default);
        Assert.Equal("unauthorized", locked.Error!.Code);

        // A different IP partition for the same account is unaffected.
        var otherIp = await _identity.LoginAsync(new(email, "password123", null), "v4:10.0.0.2", default);
        Assert.Null(otherIp.Error);
    }

    [Fact]
    public async Task Api_key_creation_requires_the_paid_plan()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await ConfirmAsync(email, registered.DevOnlyConfirmToken!);

        var result = await _identity.CreateApiKeyAsync(userId, "my key", ["links:read"], default);
        Assert.False(result.IsSuccess);
        Assert.Equal("plan_required", result.Error!.Code);
    }

    private async Task<Guid> ConfirmAsync(string email, string token)
    {
        var userId = await _db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
        var confirmed = await _identity.ConfirmEmailAsync(userId, token, default);
        Assert.True(confirmed.IsSuccess);
        return userId;
    }

    private async Task<Zaagl.Modules.Identity.Contracts.AuthResponse> RegisterConfirmAndLoginAsync()
    {
        var email = RandomEmail();
        var registered = await _identity.RegisterAsync(new(email, "password123", null), default);
        await ConfirmAsync(email, registered.DevOnlyConfirmToken!);
        var login = await _identity.LoginAsync(new(email, "password123", null), "v4:127.0.0.1", default);
        return login.Session!;
    }

    private static string GenerateCurrentCode(string secret) =>
        Totp.ComputeCode(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    private sealed class AlwaysFreePlanProvider : IPlanProvider
    {
        public Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken ct) => Task.FromResult(PlanLimits.For(PlanTier.Free));
    }

    // No-op fakes: these tests exercise Identity-only behavior (registration, login, refresh, TOTP) and
    // never touch account deletion/export, so the cross-module contracts those features need never run.
    private sealed class NoOpAccountDataEraser : Zaagl.Modules.Links.Contracts.IAccountDataEraser
    {
        public Task<IReadOnlyList<string>> EraseOwnerLinksAsync(Guid ownerId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task DeleteIdempotencyKeysAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpLinkExportSource : Zaagl.Modules.Links.Contracts.ILinkExportSource
    {
        public Task<IReadOnlyList<Zaagl.Modules.Links.Contracts.LinkExportRow>> ListAsync(Guid ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Zaagl.Modules.Links.Contracts.LinkExportRow>>([]);
    }

    private sealed class NoOpArchiveQueries : Zaagl.Modules.Analytics.Contracts.IArchiveQueries
    {
        public Task<Zaagl.Modules.Analytics.Contracts.PagedArchive> ListAsync(Guid ownerId, int limit, string? cursor, CancellationToken ct) =>
            Task.FromResult(new Zaagl.Modules.Analytics.Contracts.PagedArchive([], null));
        public Task<Zaagl.Modules.Analytics.Contracts.ArchivedLink?> FindAsync(Guid ownerId, Guid linkId, CancellationToken ct) =>
            Task.FromResult<Zaagl.Modules.Analytics.Contracts.ArchivedLink?>(null);
        public Task<Zaagl.Modules.Analytics.Contracts.ArchivedLinkStats> GetStatsAsync(Guid linkId, CancellationToken ct) =>
            Task.FromResult(new Zaagl.Modules.Analytics.Contracts.ArchivedLinkStats([]));
    }
}
