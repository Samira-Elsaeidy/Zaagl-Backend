using Zaagl.Cli.Http;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Auth;

/// <summary>How the current command is authenticated.</summary>
public enum AuthMethod
{
    None,
    ApiKey,
    EnvToken,
    StoredSession,
}

/// <summary>
/// CLI-006. Supplies the credential for each request and refreshes the stored session when needed.
/// Precedence: ZAAGL_API_KEY, then ZAAGL_TOKEN, then the OS store. Refresh is serialised in the process (a semaphore:
/// concurrent callers share one refresh) and across processes (an OS-backed lock file), and the store is re-read under
/// the lock, so N parallel <c>zaagl</c> processes at token expiry perform one rotation and never present an
/// already-rotated refresh token (which would make the server revoke the whole family).
/// </summary>
public sealed class TokenManager(
    ICredentialStore store,
    string storeKey,
    ICrossProcessLock processLock,
    ApiClient refreshClient,
    Func<string, string?> env,
    TimeProvider time,
    Func<TimeSpan, CancellationToken, Task> delay) : ICredentialProvider
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public AuthMethod Method
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(env(CredentialStores.ApiKeyVariable))) return AuthMethod.ApiKey;
            if (!string.IsNullOrWhiteSpace(env(CredentialStores.TokenVariable))) return AuthMethod.EnvToken;
            return Load() is null ? AuthMethod.None : AuthMethod.StoredSession;
        }
    }

    public StoredCredential? Load()
    {
        var blob = store.Read(storeKey);
        return blob is null ? null : StoredCredential.Deserialize(blob);
    }

    public async Task<AuthHeader> GetAsync(string? rejectedAccessToken, CancellationToken ct)
    {
        if (env(CredentialStores.ApiKeyVariable) is { Length: > 0 } key && !string.IsNullOrWhiteSpace(key))
            return AuthHeader.ApiKey(key.Trim());
        if (env(CredentialStores.TokenVariable) is { Length: > 0 } token && !string.IsNullOrWhiteSpace(token))
            return AuthHeader.Bearer(token.Trim(), refreshable: false);

        var cred = Load() ?? throw CliException.NotSignedIn();
        if (IsUsable(cred, rejectedAccessToken)) return AuthHeader.Bearer(cred.AccessToken!, refreshable: true);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var _ = await processLock
                .AcquireAsync(CredentialStores.LockNameFor(storeKey), LockTimeout, ct).ConfigureAwait(false);

            // Re-read under the lock: another process (or the caller ahead of us) may already have refreshed.
            cred = Load() ?? throw CliException.NotSignedIn();
            if (IsUsable(cred, rejectedAccessToken)) return AuthHeader.Bearer(cred.AccessToken!, refreshable: true);

            var refreshed = await RefreshAsync(cred, ct).ConfigureAwait(false);
            return AuthHeader.Bearer(refreshed.AccessToken!, refreshable: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsUsable(StoredCredential cred, string? rejected) =>
        cred.HasFreshAccessToken(time.GetUtcNow()) && !string.Equals(cred.AccessToken, rejected, StringComparison.Ordinal);

    private async Task<StoredCredential> RefreshAsync(StoredCredential current, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var uri = refreshClient.Resolve("api/auth/refresh");
                var response = await refreshClient.SendJsonAsync(
                    () => ApiClient.Json(HttpMethod.Post, uri, new RefreshRequestDto(current.RefreshToken), CliJson.Context.RefreshRequestDto),
                    CliJson.Context.AuthResponseDto, authenticate: false, RetryMode.None, ct).ConfigureAwait(false);

                var now = time.GetUtcNow();
                var next = new StoredCredential(
                    response.RefreshToken, response.RefreshTokenExpiresAt,
                    response.AccessToken, now, response.AccessTokenExpiresAt,
                    response.SessionId ?? current.SessionId,
                    response.User?.Id ?? current.UserId,
                    response.User?.Email ?? current.Email,
                    response.User?.DisplayName ?? current.DisplayName);
                store.Write(storeKey, next.Serialize());
                return next;
            }
            catch (ApiException ex) when (ex.Code == "refresh-raced" || ex.Status == 409)
            {
                // The server rotated this token a moment ago (benign window). Whoever rotated it saved the new one.
                if (attempt >= 2) throw CliException.NotSignedIn();
                await delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct).ConfigureAwait(false);
                var reread = Load() ?? throw CliException.NotSignedIn();
                if (reread.RefreshToken != current.RefreshToken && reread.HasFreshAccessToken(time.GetUtcNow())) return reread;
                current = reread;
            }
            catch (ApiException ex) when (ex.Status == 401)
            {
                // Revoked, expired or reused: the stored session is dead. Clear it so the next command says so plainly.
                var reread = Load();
                if (reread is not null && reread.RefreshToken != current.RefreshToken && reread.HasFreshAccessToken(time.GetUtcNow()))
                    return reread;
                store.Delete(storeKey);
                throw new CliException(ExitCodes.Auth, "Your session has ended. Run 'zaagl login' to sign in again.", "session-ended");
            }
        }
    }
}
