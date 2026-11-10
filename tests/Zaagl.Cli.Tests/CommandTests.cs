using System.Net;
using System.Text;
using System.Text.Json;
using Zaagl.Cli.Auth;
using Zaagl.Cli.Http;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Tests;

internal static class Wire
{
    public static string Auth(string access, string refresh, string email = "me@example.com", string expires = "2026-11-10T12:15:00Z") =>
        $"{{\"accessToken\":\"{access}\",\"accessTokenExpiresAt\":\"{expires}\",\"refreshToken\":\"{refresh}\"," +
        "\"refreshTokenExpiresAt\":\"2026-12-10T12:00:00Z\"," +
        $"\"user\":{{\"id\":\"22222222-2222-2222-2222-222222222222\",\"email\":\"{email}\",\"displayName\":\"Me\",\"role\":\"User\",\"emailConfirmed\":true}}," +
        "\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}";

    public const string DeviceCode =
        "{\"device_code\":\"dc-secret\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://app.test.local/activate\"," +
        "\"verification_uri_complete\":\"https://app.test.local/activate?code=ABCD-EFGH\",\"expires_in\":600,\"interval\":5}";

    public static HttpResponseMessage OAuthError(string error) =>
        FakeServer.Json(HttpStatusCode.BadRequest, $"{{\"error\":\"{error}\"}}");

    public const string Snippet =
        "{\"resourceId\":\"33333333-3333-3333-3333-333333333333\",\"code\":\"abc123\",\"shortUrl\":\"https://zaa.gl/abc123\"," +
        "\"linkKey\":\"KEY1\",\"version\":1,\"sizeBytes\":5,\"lineCount\":1,\"expiresAt\":\"2026-11-17T12:00:00Z\",\"isGuest\":false}";
}

public class LoginLogoutWhoamiTests
{
    [Fact]
    public async Task Login_polls_through_pending_and_slow_down_then_stores_credential_only_in_the_os_store()
    {
        var polls = 0;
        using var env = new TestEnv(r => r.Path switch
        {
            "/api/auth/device/code" => FakeServer.Json(HttpStatusCode.OK, Wire.DeviceCode),
            "/api/auth/device/token" => ++polls switch
            {
                1 => Wire.OAuthError("authorization_pending"),
                2 => Wire.OAuthError("slow_down"),
                _ => FakeServer.Json(HttpStatusCode.OK, Wire.Auth("at-secret", "rt-secret")),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var exit = await env.Run("login", "--no-browser");

        Assert.Equal(0, exit);
        Assert.Equal(3, polls);
        Assert.Equal(new[] { 5, 5, 10 }, env.Delays.Select(d => (int)d.TotalSeconds).ToArray()); // slow_down adds 5 s
        Assert.Contains("ABCD-EFGH", env.Console.Stderr);
        Assert.Contains("Signed in as me@example.com", env.Console.Stdout);
        Assert.Equal(1, env.Store.Count);
        var stored = StoredCredential.Deserialize(env.Store.Read(env.StoreKey)!)!;
        Assert.Equal("rt-secret", stored.RefreshToken);
        Assert.Empty(env.Browser.Opened);

        // CLI-004: no token on disk. Scan every file the CLI could have written.
        foreach (var file in Directory.EnumerateFiles(env.Dir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".lock")) continue;
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("rt-secret", text);
            Assert.DoesNotContain("at-secret", text);
        }
        var device = env.Server.Requests[0];
        Assert.Contains("\"client_id\":\"zaagl-cli\"", device.BodyText);
    }

    [Fact]
    public async Task Login_opens_the_browser_when_possible()
    {
        using var env = new TestEnv(r => r.Path == "/api/auth/device/code"
            ? FakeServer.Json(HttpStatusCode.OK, Wire.DeviceCode)
            : FakeServer.Json(HttpStatusCode.OK, Wire.Auth("a", "r")));
        env.Browser.Available = true;
        Assert.Equal(0, await env.Run("login"));
        Assert.Equal("https://app.test.local/activate?code=ABCD-EFGH", env.Browser.Opened.Single().ToString());
    }

    [Theory]
    [InlineData("access_denied", 3)]
    [InlineData("expired_token", 3)]
    public async Task Login_failures_exit_3_and_store_nothing(string error, int exitCode)
    {
        using var env = new TestEnv(r => r.Path == "/api/auth/device/code"
            ? FakeServer.Json(HttpStatusCode.OK, Wire.DeviceCode)
            : Wire.OAuthError(error));
        Assert.Equal(exitCode, await env.Run("login", "--no-browser"));
        Assert.Equal(0, env.Store.Count);
    }

    [Fact]
    public async Task Login_gives_up_when_the_code_lifetime_passes()
    {
        using var env = new TestEnv(r => r.Path == "/api/auth/device/code"
            ? FakeServer.Json(HttpStatusCode.OK, Wire.DeviceCode.Replace("\"expires_in\":600", "\"expires_in\":10"))
            : Wire.OAuthError("authorization_pending"));
        Assert.Equal(3, await env.Run("login", "--no-browser"));
        Assert.Equal(2, env.Server.Requests.Count(r => r.Path.EndsWith("/token")));
    }

    [Fact]
    public async Task Login_without_an_os_store_exits_3_with_env_var_guidance()
    {
        using var env = new TestEnv(r => r.Path == "/api/auth/device/code"
            ? FakeServer.Json(HttpStatusCode.OK, Wire.DeviceCode)
            : FakeServer.Json(HttpStatusCode.OK, Wire.Auth("a", "r")));
        var services = new Zaagl.Cli.Commands.CliServices
        {
            Console = env.Console, Env = env.Services.Env, Store = new UnavailableCredentialStore(), Lock = env.Services.Lock,
            Browser = env.Browser, ConfigDirectory = env.Dir, Time = env.Clock, Delay = env.Services.Delay, HttpHandler = env.Services.HttpHandler,
        };
        var exit = await Zaagl.Cli.Commands.CliApp.RunAsync(["--api-url", TestEnv.Api, "login", "--no-browser"], services, CancellationToken.None);
        Assert.Equal(3, exit);
        Assert.Contains("ZAAGL_TOKEN", env.Console.Stderr);
    }

    [Fact]
    public async Task Logout_revokes_then_wipes_the_entry()
    {
        using var env = new TestEnv(r => new HttpResponseMessage(HttpStatusCode.NoContent));
        env.SignIn(refresh: "rt-1");
        Assert.Equal(0, await env.Run("logout"));
        Assert.Equal(0, env.Store.Count);
        var call = env.Server.Requests.Single();
        Assert.Equal("/api/auth/logout", call.Path);
        Assert.Contains("rt-1", call.BodyText);
    }

    [Fact]
    public async Task Logout_still_wipes_when_the_server_is_unreachable_and_says_so()
    {
        using var env = new TestEnv(r => FakeServer.Problem(HttpStatusCode.ServiceUnavailable, "service-unavailable"));
        env.SignIn();
        Assert.Equal(0, await env.Run("logout", "--json"));
        Assert.Equal(0, env.Store.Count);
        Assert.Contains("Could not revoke", env.Console.Stderr);
        Assert.Contains("\"revoked\":false", env.Console.Stdout.Replace(" ", ""));
    }

    [Fact]
    public async Task Whoami_when_signed_out_exits_3()
    {
        using var env = new TestEnv();
        Assert.Equal(3, await env.Run("whoami"));
        Assert.Contains("zaagl login", env.Console.Stderr);
    }

    [Fact]
    public async Task Whoami_shows_stored_identity_and_plan_limits_when_whoami_endpoint_is_missing()
    {
        using var env = new TestEnv(r => r.Path == "/api/me/storage"
            ? FakeServer.Json(HttpStatusCode.OK, "{\"limits\":{\"maxSnippetBytes\":524288,\"retentionDays\":30},\"usedBytes\":0,\"reservedBytes\":0}")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        env.SignIn();
        Assert.Equal(0, await env.Run("whoami", "--json"));
        using var doc = JsonDocument.Parse(env.Console.Stdout);
        Assert.Equal("me@example.com", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("device-session", doc.RootElement.GetProperty("authMethod").GetString());
        Assert.Equal(524288, doc.RootElement.GetProperty("maxSnippetBytes").GetInt64());
    }

    [Fact]
    public async Task Api_key_in_the_environment_takes_precedence_and_is_sent_in_the_header()
    {
        using var env = new TestEnv(r => FakeServer.Json(HttpStatusCode.OK, "{\"id\":\"22222222-2222-2222-2222-222222222222\",\"email\":\"k@example.com\"}"),
            env: new() { ["ZAAGL_API_KEY"] = "zgl_testkey" });
        env.SignIn();
        Assert.Equal(0, await env.Run("whoami", "--json"));
        Assert.All(env.Server.Requests, r => Assert.Equal("zgl_testkey", r.Headers["X-Api-Key"]));
        Assert.Contains("api-key", env.Console.Stdout);
    }
}

public class LinkTests
{
    [Fact]
    public async Task Link_posts_the_destination_with_an_idempotency_key_and_prints_only_the_url()
    {
        using var env = new TestEnv(r => FakeServer.Json(HttpStatusCode.Created,
            "{\"id\":\"44444444-4444-4444-4444-444444444444\",\"slug\":\"promo\",\"destinationUrl\":\"https://example.com/a\",\"isActive\":true,\"createdAt\":\"2026-11-10T12:00:00Z\",\"shortUrl\":\"https://zaa.gl/promo\"}"));
        env.SignIn();
        Assert.Equal(0, await env.Run("link", "https://example.com/a", "--slug", "promo", "--expires", "7d"));
        Assert.Equal("https://zaa.gl/promo\n", env.Console.Stdout);
        var call = env.Server.Requests.Single();
        Assert.Equal("Bearer at-1", call.Headers["Authorization"]);
        Assert.False(string.IsNullOrEmpty(call.Headers["Idempotency-Key"]));
        using var body = JsonDocument.Parse(call.BodyText);
        Assert.Equal("promo", body.RootElement.GetProperty("customSlug").GetString());
        Assert.Equal("2026-11-17T12:00:00+00:00", body.RootElement.GetProperty("policy").GetProperty("expiresAt").GetDateTimeOffset().ToString("yyyy-MM-dd'T'HH:mm:sszzz"));
    }

    [Fact]
    public async Task Link_falls_back_to_a_derived_short_url_when_the_server_omits_it()
    {
        using var env = new TestEnv(r => FakeServer.Json(HttpStatusCode.Created,
            "{\"id\":\"44444444-4444-4444-4444-444444444444\",\"slug\":\"abcd\",\"isActive\":true,\"createdAt\":\"2026-11-10T12:00:00Z\"}"));
        env.SignIn();
        Assert.Equal(0, await env.Run("link", "https://example.com"));
        Assert.Equal("https://test.local/abcd\n", env.Console.Stdout);
        Assert.Contains("did not return the short URL", env.Console.Stderr);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "plan-required", 4)]
    [InlineData(HttpStatusCode.Forbidden, "insufficient-scope", 3)]
    [InlineData(HttpStatusCode.Conflict, "slug-taken", 8)]
    [InlineData(HttpStatusCode.TooManyRequests, "too-many-requests", 7)]
    public async Task Link_maps_problem_codes_to_exit_codes(HttpStatusCode status, string code, int exit)
    {
        using var env = new TestEnv(r => FakeServer.Problem(status, code));
        env.SignIn();
        Assert.Equal(exit, await env.Run("link", "https://example.com"));
    }

    [Fact]
    public async Task Link_rejects_bad_input_with_exit_2_before_any_request()
    {
        using var env = new TestEnv();
        env.SignIn();
        Assert.Equal(2, await env.Run("link", "ftp://example.com"));
        Assert.Equal(2, await env.Run("link"));
        Assert.Equal(2, await env.Run("link", "https://example.com", "--expires", "soon"));
        Assert.Empty(env.Server.Requests);
    }

    [Fact]
    public async Task Link_json_mode_prints_one_object_and_errors_as_an_envelope()
    {
        using var env = new TestEnv(r => FakeServer.Problem(HttpStatusCode.PaymentRequired, "plan-required", "Upgrade."));
        env.SignIn();
        Assert.Equal(4, await env.Run("link", "https://example.com", "--json"));
        using var doc = JsonDocument.Parse(env.Console.Stdout);
        Assert.Equal("plan-required", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("error").GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Not_signed_in_exits_3_without_a_request()
    {
        using var env = new TestEnv();
        Assert.Equal(3, await env.Run("link", "https://example.com"));
        Assert.Empty(env.Server.Requests);
    }

    [Fact]
    public async Task Transient_503_is_retried_for_a_create_that_carries_an_idempotency_key()
    {
        var n = 0;
        using var env = new TestEnv(r => ++n < 3
            ? FakeServer.Problem(HttpStatusCode.ServiceUnavailable, "service-unavailable")
            : FakeServer.Json(HttpStatusCode.Created, "{\"id\":\"44444444-4444-4444-4444-444444444444\",\"slug\":\"x1x1\",\"isActive\":true,\"createdAt\":\"2026-11-10T12:00:00Z\",\"shortUrl\":\"https://zaa.gl/x1x1\"}"));
        env.SignIn();
        Assert.Equal(0, await env.Run("link", "https://example.com"));
        Assert.Equal(3, env.Server.Requests.Count);
        Assert.Single(env.Server.Requests.Select(r => r.Headers["Idempotency-Key"]).Distinct());
    }

    [Fact]
    public async Task A_400_is_never_retried()
    {
        using var env = new TestEnv(r => FakeServer.Problem(HttpStatusCode.BadRequest, "validation"));
        env.SignIn();
        Assert.Equal(8, await env.Run("link", "https://example.com"));
        Assert.Single(env.Server.Requests);
    }

    [Fact]
    public async Task Network_failure_exits_6_after_retries()
    {
        using var env = new TestEnv(r => throw new HttpRequestException("down"));
        env.SignIn();
        Assert.Equal(6, await env.Run("link", "https://example.com"));
        Assert.Equal(ApiClient.MaxRetries + 1, env.Server.Requests.Count);
    }
}

public class PasteTests
{
    private static TestEnv Env(byte[] stdin, Func<Seen, HttpResponseMessage>? raw = null)
    {
        var env = new TestEnv(r => r.Path switch
        {
            "/api/snippets/raw" => raw?.Invoke(r) ?? FakeServer.Json(HttpStatusCode.Created, Wire.Snippet),
            "/api/me/storage" => FakeServer.Json(HttpStatusCode.OK, "{\"limits\":{\"maxSnippetBytes\":100,\"retentionDays\":7},\"usedBytes\":0,\"reservedBytes\":0}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, stdin);
        env.SignIn();
        return env;
    }

    [Fact]
    public async Task Utf8_input_is_uploaded_as_text_plain_utf8_and_the_link_includes_the_key_fragment()
    {
        using var env = Env(Encoding.UTF8.GetBytes("héllo\n"));
        Assert.Equal(0, await env.Run("paste", "--lang", "log", "--title", "T 1"));
        Assert.Equal("https://zaa.gl/abc123#k=KEY1\n", env.Console.Stdout);
        var call = env.Server.Requests.Single(r => r.Path == "/api/snippets/raw");
        Assert.StartsWith("text/plain", call.ContentType);
        Assert.Contains("utf-8", call.ContentType);
        Assert.Equal("héllo\n", call.BodyText);
        Assert.Contains("lang=log", call.Uri.Query);
        Assert.Contains("title=T%201", call.Uri.Query);
        Assert.False(string.IsNullOrEmpty(call.Headers["Idempotency-Key"]));
    }

    [Fact]
    public async Task Powershell_utf16le_with_bom_is_decoded_and_sent_as_utf8()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("line one\r\nline two ✓\r\n")).ToArray();
        using var env = Env(utf16);
        Assert.Equal(0, await env.Run("paste"));
        var call = env.Server.Requests.Single(r => r.Path == "/api/snippets/raw");
        Assert.Equal("line one\r\nline two ✓\r\n", call.BodyText); // bytes kept exactly, CRLF included
        Assert.DoesNotContain((byte)0, call.Body);
    }

    [Fact]
    public async Task Utf16_without_a_bom_is_detected()
    {
        using var env = Env(Encoding.Unicode.GetBytes("plain ascii text from a pipe\n"));
        Assert.Equal(0, await env.Run("paste"));
        Assert.Equal("plain ascii text from a pipe\n", env.Server.Requests.Single(r => r.Path == "/api/snippets/raw").BodyText);
    }

    [Fact]
    public async Task Ansi_is_stripped_by_default_and_kept_as_terminal_with_keep_ansi()
    {
        using var stripped = Env(Encoding.UTF8.GetBytes("\u001b[31mred\u001b[0m text\n"));
        Assert.Equal(0, await stripped.Run("paste"));
        Assert.Equal("red text\n", stripped.Server.Requests.Single(r => r.Path == "/api/snippets/raw").BodyText);

        using var kept = Env(Encoding.UTF8.GetBytes("\u001b[31mred\u001b[0m\n"));
        Assert.Equal(0, await kept.Run("paste", "--keep-ansi"));
        var call = kept.Server.Requests.Single(r => r.Path == "/api/snippets/raw");
        Assert.Contains("\u001b[31m", call.BodyText);
        Assert.Contains("renderMode=terminal", call.Uri.Query);
        Assert.Contains("ansi=keep", call.Uri.Query);
    }

    [Fact]
    public async Task Oversize_input_exits_8_and_never_calls_the_upload_endpoint()
    {
        using var env = Env(new string('x', 101).Select(c => (byte)c).ToArray()); // plan cap is 100 bytes in this fake
        Assert.Equal(8, await env.Run("paste"));
        Assert.DoesNotContain(env.Server.Requests, r => r.Path == "/api/snippets/raw");
        Assert.Contains("larger than", env.Console.Stderr);
    }

    [Fact]
    public async Task Binary_input_exits_8()
    {
        using var env = Env([0x00, 0x01, 0x02, 0x00, 0x03]);
        Assert.Equal(8, await env.Run("paste"));
        Assert.DoesNotContain(env.Server.Requests, r => r.Path == "/api/snippets/raw");
    }

    [Fact]
    public async Task Empty_or_terminal_input_is_a_usage_error()
    {
        using var empty = Env([]);
        Assert.Equal(2, await empty.Run("paste"));
        using var tty = new TestEnv(inputRedirected: false);
        tty.SignIn();
        Assert.Equal(2, await tty.Run("paste"));
    }

    [Fact]
    public async Task Tee_copies_the_input_to_stderr_bytes()
    {
        using var env = Env(Encoding.UTF8.GetBytes("abc\n"));
        Assert.Equal(0, await env.Run("paste", "--tee"));
        Assert.Equal("abc\n", Encoding.UTF8.GetString(env.Console.TeeBytes.ToArray()));
    }

    [Fact]
    public async Task Server_413_maps_to_exit_8_and_415_to_exit_8()
    {
        using var a = Env(Encoding.UTF8.GetBytes("x"), _ => FakeServer.Problem(HttpStatusCode.RequestEntityTooLarge, "snippet-too-large"));
        Assert.Equal(8, await a.Run("paste"));
        using var b = Env(Encoding.UTF8.GetBytes("x"), _ => FakeServer.Problem(HttpStatusCode.UnsupportedMediaType, "unsupported-media-type"));
        Assert.Equal(8, await b.Run("paste"));
    }

    [Fact]
    public async Task Json_mode_prints_the_result_object()
    {
        using var env = Env(Encoding.UTF8.GetBytes("x"));
        Assert.Equal(0, await env.Run("paste", "--json"));
        using var doc = JsonDocument.Parse(env.Console.Stdout);
        Assert.Equal("abc123", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://zaa.gl/abc123#k=KEY1", doc.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Quiet_mode_prints_only_the_url_and_no_progress()
    {
        using var env = Env(Encoding.UTF8.GetBytes("x"));
        Assert.Equal(0, await env.Run("paste", "--quiet"));
        Assert.Equal("https://zaa.gl/abc123#k=KEY1\n", env.Console.Stdout);
        Assert.Equal("", env.Console.Stderr);
    }
}

public class LsAndConfigTests
{
    [Fact]
    public async Task Ls_json_is_json_lines_with_one_row_per_line_and_cursor_on_stderr()
    {
        using var env = new TestEnv(r => FakeServer.Json(HttpStatusCode.OK,
            "{\"items\":[" +
            "{\"id\":\"33333333-3333-3333-3333-333333333333\",\"kind\":\"snippet\",\"title\":\"A\",\"code\":\"aaa111\",\"shortUrl\":\"https://zaa.gl/aaa111\",\"state\":\"ready\",\"sizeBytes\":12,\"expiresAt\":\"2026-11-17T12:00:00Z\",\"burnAfterDownload\":false,\"isPublic\":false,\"createdAt\":\"2026-11-10T12:00:00Z\"}," +
            "{\"id\":\"33333333-3333-3333-3333-333333333334\",\"kind\":\"file\",\"code\":\"bbb222\",\"shortUrl\":\"https://zaa.gl/bbb222\",\"state\":\"ready\",\"sizeBytes\":2048,\"expiresAt\":\"2026-11-17T12:00:00Z\",\"burnAfterDownload\":false,\"isPublic\":true,\"createdAt\":\"2026-11-10T12:00:00Z\"}]," +
            "\"nextCursor\":\"CUR\"}"));
        env.SignIn();
        Assert.Equal(0, await env.Run("ls", "--json", "--limit", "2"));
        var lines = env.Console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        foreach (var l in lines) JsonDocument.Parse(l).Dispose();
        Assert.Contains("--cursor CUR", env.Console.Stderr);
        Assert.Contains("limit=2", env.Server.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task Ls_human_prints_a_table_and_type_link_uses_the_links_endpoint()
    {
        using var env = new TestEnv(r => FakeServer.Json(HttpStatusCode.OK,
            "{\"items\":[{\"link\":{\"id\":\"44444444-4444-4444-4444-444444444444\",\"slug\":\"promo\",\"isActive\":true,\"createdAt\":\"2026-11-10T12:00:00Z\",\"shortUrl\":\"https://zaa.gl/promo\"},\"clicks\":7}],\"total\":1,\"page\":1,\"pageSize\":20}"));
        env.SignIn();
        Assert.Equal(0, await env.Run("ls", "--type", "link"));
        Assert.Equal("/api/links", env.Server.Requests[0].Path);
        Assert.Contains("https://zaa.gl/promo", env.Console.Stdout);
        Assert.Contains("7 clicks", env.Console.Stdout);
    }

    [Fact]
    public async Task Ls_rejects_bad_options()
    {
        using var env = new TestEnv();
        env.SignIn();
        Assert.Equal(2, await env.Run("ls", "--type", "video"));
        Assert.Equal(2, await env.Run("ls", "--limit", "0"));
        Assert.Equal(2, await env.Run("ls", "--page", "2"));
    }

    [Fact]
    public async Task Config_set_get_list_unset_round_trip_and_refuse_unknown_keys_and_secrets()
    {
        using var env = new TestEnv();
        Assert.Equal(0, await env.Run("config", "set", "default-expiry", "7d"));
        Assert.Equal(0, await env.Run("config", "get", "default-expiry"));
        Assert.EndsWith("7d\n", env.Console.Stdout);
        Assert.Equal(2, await env.Run("config", "set", "nope", "x"));
        Assert.Equal(2, await env.Run("config", "set", "api-url", "zgl_abcdef"));
        Assert.Equal(2, await env.Run("config", "set", "api-url", "http://example.com")); // http only for loopback
        Assert.Equal(0, await env.Run("config", "list"));
        Assert.Contains("default-expiry = 7d", env.Console.Stdout);
        Assert.Equal(0, await env.Run("config", "unset", "default-expiry"));
        Assert.DoesNotContain("default-expiry", File.ReadAllText(Path.Combine(env.Dir, "config.json")));
    }

    [Fact]
    public async Task Unknown_command_and_help_and_version()
    {
        using var env = new TestEnv();
        Assert.Equal(2, await env.Run("frobnicate"));
        Assert.Equal(2, await env.Run("link", "--bogus"));
        Assert.Equal(0, await env.Run("--help"));
        Assert.Contains("Commands:", env.Console.Stdout);
        Assert.Equal(0, await env.Run("version", "--json"));
        Assert.Contains("\"version\"", env.Console.Stdout);
    }
}
