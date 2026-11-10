using System.Text.Json;
using Zaagl.Cli.Auth;
using Zaagl.Cli.Http;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Commands;

/// <summary>CLI-005: RFC 8628 device authorization (contracts.md C9.2). The CLI holds no secret: the device code is
/// useless without a person approving the user code in a signed-in browser session.</summary>
public static class LoginCommand
{
    public static readonly CommandSpec Spec = new(
        "login", "Sign in with your browser (device authorization).", "zaagl login [--no-browser]",
        [new OptionSpec("no-browser", false, null, "Only print the address and code; do not open a browser.")],
        RunAsync);

    private static async Task<int> RunAsync(CommandContext ctx, CancellationToken ct)
    {
        var o = ctx.Output;
        if (ctx.Args.Positionals.Count > 0) throw CliException.Usage("'login' takes no arguments.");

        var api = ctx.Anonymous;
        var codeUri = api.Resolve("api/auth/device/code");
        var request = new DeviceCodeRequestDto(DeviceFlowWire.ClientId, PlatformInfo.DeviceName(ctx.Services.Env), PlatformInfo.DeviceOs);
        var code = await api.SendJsonAsync(
            () => ApiClient.Json(HttpMethod.Post, codeUri, request, CliJson.Context.DeviceCodeRequestDto),
            CliJson.Context.DeviceCodeResponseDto, authenticate: false, RetryMode.Idempotent, ct).ConfigureAwait(false);

        var verifyUrl = code.VerificationUriComplete ?? code.VerificationUri;
        var userCode = TextFormat.Safe(code.UserCode);
        // The prompt goes to stderr even in --json mode: stdout stays machine-readable, and a person has to read it.
        o.Console.Error.WriteLine($"To sign in, open {TextFormat.Safe(code.VerificationUri)} and enter the code: {userCode}");

        if (!ctx.Args.Has("no-browser") && ctx.Services.Browser.CanOpen(ctx.Services.Env)
            && Uri.TryCreate(verifyUrl, UriKind.Absolute, out var verifyUri))
        {
            if (ctx.Services.Browser.TryOpen(verifyUri)) o.Info("Opened your browser. Approve the request there.");
        }
        o.Info($"Waiting for approval (the code expires in {code.ExpiresIn / 60} minutes)...");

        var auth = await PollAsync(ctx, code, ct).ConfigureAwait(false);

        var cred = new StoredCredential(
            auth.RefreshToken, auth.RefreshTokenExpiresAt, auth.AccessToken, ctx.Services.Time.GetUtcNow(),
            auth.AccessTokenExpiresAt, auth.SessionId, auth.User?.Id, auth.User?.Email, auth.User?.DisplayName);
        // Fails with exit 3 and env-var guidance when no OS store exists; nothing is written anywhere else.
        ctx.Services.Store.Write(ctx.StoreKey, cred.Serialize());

        var host = ctx.ApiBase.Host;
        if (o.Mode == OutputMode.Json)
            o.Json(new LoginResult(cred.Email, cred.DisplayName, host), CliJson.Context.LoginResult);
        else if (o.Mode == OutputMode.Quiet)
            o.Result(cred.Email ?? "signed-in");
        else
            o.Result($"Signed in{(cred.Email is null ? "" : " as " + TextFormat.Safe(cred.Email))} on {host}.");

        if (!string.IsNullOrWhiteSpace(ctx.Services.Env(CredentialStores.ApiKeyVariable)))
            o.Warn($"{CredentialStores.ApiKeyVariable} is set and takes precedence over this sign-in.");
        else if (!string.IsNullOrWhiteSpace(ctx.Services.Env(CredentialStores.TokenVariable)))
            o.Warn($"{CredentialStores.TokenVariable} is set and takes precedence over this sign-in.");
        return ExitCodes.Ok;
    }

    private static async Task<AuthResponseDto> PollAsync(CommandContext ctx, DeviceCodeResponseDto code, CancellationToken ct)
    {
        var api = ctx.Anonymous;
        var tokenUri = api.Resolve("api/auth/device/token");
        var body = new DeviceTokenRequestDto(DeviceFlowWire.GrantType, code.DeviceCode, DeviceFlowWire.ClientId);
        var interval = Math.Max(code.Interval, 1);
        var waited = 0;
        var lifetime = code.ExpiresIn > 0 ? code.ExpiresIn : 600;

        while (true)
        {
            // Time is counted from the waits themselves, so a test with a fake delay finishes at once.
            await ctx.Services.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            waited += interval;
            try
            {
                return await api.SendJsonAsync(
                    () => ApiClient.Json(HttpMethod.Post, tokenUri, body, CliJson.Context.DeviceTokenRequestDto),
                    CliJson.Context.AuthResponseDto, authenticate: false, RetryMode.None, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Code == "authorization-pending")
            {
                // keep polling
            }
            catch (ApiException ex) when (ex.Code == "slow-down")
            {
                interval += DeviceFlowWire.SlowDownIncrementSeconds;
            }
            catch (ApiException ex) when (ex.Status == 429)
            {
                interval += DeviceFlowWire.SlowDownIncrementSeconds;
            }
            catch (ApiException ex) when (ex.Code == "access-denied")
            {
                throw new CliException(ExitCodes.Auth, "Sign-in was denied in the browser.", "access-denied");
            }
            catch (ApiException ex) when (ex.Code is "expired-token" or "invalid-grant")
            {
                throw new CliException(ExitCodes.Auth, "The sign-in code expired. Run 'zaagl login' again.", "expired-token");
            }
            // Anything else (invalid_client, 5xx, network) propagates with its own exit code.

            if (waited >= lifetime)
                throw new CliException(ExitCodes.Auth, "The sign-in code expired. Run 'zaagl login' again.", "expired-token");
        }
    }
}

public static class LogoutCommand
{
    public static readonly CommandSpec Spec = new(
        "logout", "Sign out: revoke this device's session and remove it from the OS credential store.", "zaagl logout", [], RunAsync);

    private static async Task<int> RunAsync(CommandContext ctx, CancellationToken ct)
    {
        var o = ctx.Output;
        var cred = ctx.Tokens.Load();
        var revoked = false;
        if (cred is not null)
        {
            try
            {
                var uri = ctx.Anonymous.Resolve("api/auth/logout");
                using var _ = await ctx.Anonymous.SendAsync(
                    () => ApiClient.Json(HttpMethod.Post, uri, new LogoutRequestDto(cred.RefreshToken), CliJson.Context.LogoutRequestDto),
                    authenticate: false, RetryMode.Idempotent, ct).ConfigureAwait(false);
                revoked = true;
            }
            catch (CliException ex) when (ex is ApiException { Status: 401 })
            {
                revoked = true; // already invalid on the server: nothing left to revoke
            }
            catch (CliException ex)
            {
                o.Warn($"Could not revoke the session on the server ({ex.Message}). It was removed from this device; you can revoke it from the web app.");
            }
            ctx.Services.Store.Delete(ctx.StoreKey);
        }

        if (o.Mode == OutputMode.Json)
            o.Json(new LogoutResult(cred is not null, revoked), CliJson.Context.LogoutResult);
        else if (o.Mode == OutputMode.Human)
            o.Result(cred is null ? "You were not signed in." : "Signed out.");

        if (!string.IsNullOrWhiteSpace(ctx.Services.Env(CredentialStores.ApiKeyVariable)) || !string.IsNullOrWhiteSpace(ctx.Services.Env(CredentialStores.TokenVariable)))
            o.Warn($"{CredentialStores.ApiKeyVariable}/{CredentialStores.TokenVariable} is still set in your environment; unset it to stop using it.");
        return ExitCodes.Ok;
    }
}

public static class WhoamiCommand
{
    public static readonly CommandSpec Spec = new(
        "whoami", "Show the account, authentication method and plan limits.", "zaagl whoami", [], RunAsync);

    private static async Task<int> RunAsync(CommandContext ctx, CancellationToken ct)
    {
        var o = ctx.Output;
        var method = ctx.Tokens.Method;
        if (method == AuthMethod.None) throw CliException.NotSignedIn();

        var stored = method == AuthMethod.StoredSession ? ctx.Tokens.Load() : null;
        string? email = stored?.Email, name = stored?.DisplayName, role = null;

        // GET /api/auth/whoami is PROPOSED (ARC-014 §3.1: /api/auth/me refuses device tokens). Until it exists the stored
        // identity from sign-in is shown, and only the storage call below proves the credential works.
        try
        {
            var uri = ctx.Api.Resolve("api/auth/whoami");
            var me = await ctx.Api.SendJsonAsync(() => ApiClient.Get(uri), CliJson.Context.WhoamiDto, true, RetryMode.Idempotent, ct).ConfigureAwait(false);
            email = me.Email ?? email;
            name = me.DisplayName ?? name;
            role = me.Role;
        }
        catch (ApiException ex) when (ex.Status is 404 or 405)
        {
            // endpoint not deployed yet
        }

        long? maxSnippet = null;
        int? retention = null;
        try
        {
            var uri = ctx.Api.Resolve("api/me/storage");
            var s = await ctx.Api.SendJsonAsync(() => ApiClient.Get(uri), CliJson.Context.StorageDto, true, RetryMode.Idempotent, ct).ConfigureAwait(false);
            maxSnippet = s.Limits?.MaxSnippetBytes;
            retention = s.Limits?.RetentionDays;
        }
        catch (ApiException ex) when (ex.Status is 404 or 405 || ex.Code == "insufficient-scope")
        {
            // limits unavailable; not fatal for whoami
        }

        var methodName = method switch { AuthMethod.ApiKey => "api-key", AuthMethod.EnvToken => "env-token", _ => "device-session" };
        var result = new WhoamiResult(email, name, role, methodName, ctx.ApiBase.Host, stored?.SessionId, maxSnippet, retention);
        if (o.Mode == OutputMode.Json) o.Json(result, CliJson.Context.WhoamiResult);
        else if (o.Mode == OutputMode.Quiet) o.Result(email ?? methodName);
        else
        {
            var shownEmail = TextFormat.Safe(email);
            var shownName = TextFormat.Safe(name);
            o.Result("Account: " + (shownEmail.Length > 0 ? shownEmail : "(unknown)") + (shownName.Length > 0 ? " (" + shownName + ")" : ""));
            o.Result($"Signed in with: {methodName} on {result.Host}");
            if (maxSnippet is { } m) o.Result($"Max snippet: {TextFormat.Bytes(m)}");
            if (retention is { } r) o.Result($"Retention: {r.ToString(System.Globalization.CultureInfo.InvariantCulture)} days");
        }
        return ExitCodes.Ok;
    }
}
