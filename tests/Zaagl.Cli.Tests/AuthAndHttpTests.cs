using System.Net;
using System.Text.Json;
using Zaagl.Cli.Auth;
using Zaagl.Cli.Http;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Tests;

public class TokenRefreshTests
{
    /// <summary>A server that rotates refresh tokens and, like the real one, revokes the family on reuse.</summary>
    private sealed class RotatingServer
    {
        public FakeClock? Clock;
        public string CurrentRefresh = "rt-1";
        public int Rotations;
        public bool FamilyRevoked;
        private int _seq = 1;

        public HttpResponseMessage Handle(Seen r)
        {
            if (r.Path != "/api/auth/refresh") return new HttpResponseMessage(HttpStatusCode.NotFound);
            Thread.Sleep(20); // widen the race window
            lock (this)
            {
                var presented = JsonDocument.Parse(r.BodyText).RootElement.GetProperty("refreshToken").GetString();
                if (FamilyRevoked || presented != CurrentRefresh)
                {
                    FamilyRevoked = true;
                    return FakeServer.Problem(HttpStatusCode.Unauthorized, "unauthorized", "reuse");
                }
                Rotations++;
                _seq++;
                CurrentRefresh = "rt-" + _seq;
                return FakeServer.Json(HttpStatusCode.OK, Wire.Auth("at-" + _seq, CurrentRefresh, expires: (Clock?.Now ?? TestEnv.Start).AddMinutes(15).ToString("O")));
            }
        }
    }

    private static TokenManager Manager(TestEnv env, RotatingServer server, MemoryCredentialStore? store = null)
    {
        var handler = new FakeServer(server.Handle);
        var http = ApiClient.CreateHttpClient(handler);
        var anonymous = new ApiClient(http, new Uri(TestEnv.Api), null, env.Services.Delay);
        return new TokenManager(store ?? env.Store, env.StoreKey, new FileCrossProcessLock(env.Dir), anonymous, env.Services.Env, env.Clock, env.Services.Delay);
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_once_and_saved()
    {
        using var env = new TestEnv();
        env.SignIn(accessLife: TimeSpan.FromMinutes(15));
        env.Clock.Now = TestEnv.Start.AddMinutes(20);
        var server = new RotatingServer { Clock = env.Clock };
        var header = await Manager(env, server).GetAsync(null, CancellationToken.None);
        Assert.Equal("at-2", header.Value);
        Assert.Equal("rt-2", StoredCredential.Deserialize(env.Store.Read(env.StoreKey)!)!.RefreshToken);
        // The next caller reuses the saved token: no second rotation.
        await Manager(env, server).GetAsync(null, CancellationToken.None);
        Assert.Equal(1, server.Rotations);
    }

    [Fact]
    public async Task Access_token_is_refreshed_proactively_at_80_percent_of_its_life()
    {
        using var env = new TestEnv();
        env.SignIn(accessLife: TimeSpan.FromMinutes(10));
        var server = new RotatingServer { Clock = env.Clock };
        env.Clock.Now = TestEnv.Start.AddMinutes(7); // before 80 %
        Assert.Equal("at-1", (await Manager(env, server).GetAsync(null, CancellationToken.None)).Value);
        env.Clock.Now = TestEnv.Start.AddMinutes(8.5); // after 80 %
        Assert.Equal("at-2", (await Manager(env, server).GetAsync(null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Ten_parallel_managers_at_expiry_rotate_exactly_once_and_never_revoke_the_family()
    {
        using var env = new TestEnv();
        env.SignIn();
        env.Clock.Now = TestEnv.Start.AddMinutes(20);
        var server = new RotatingServer { Clock = env.Clock };
        // Ten managers with their own in-process state share only the store and the lock file: ten "processes".
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() => Manager(env, server).GetAsync(null, CancellationToken.None))).ToArray();
        var headers = await Task.WhenAll(tasks);
        Assert.False(server.FamilyRevoked);
        Assert.Equal(1, server.Rotations);
        Assert.All(headers, h => Assert.Equal("at-2", h.Value));
    }

    [Fact]
    public async Task One_manager_with_many_concurrent_callers_refreshes_once()
    {
        using var env = new TestEnv();
        env.SignIn();
        env.Clock.Now = TestEnv.Start.AddMinutes(20);
        var server = new RotatingServer { Clock = env.Clock };
        var manager = Manager(env, server);
        var headers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => manager.GetAsync(null, CancellationToken.None)));
        Assert.Equal(1, server.Rotations);
        Assert.All(headers, h => Assert.Equal("at-2", h.Value));
    }

    [Fact]
    public async Task A_rejected_token_forces_a_refresh_even_when_it_looks_fresh()
    {
        using var env = new TestEnv();
        env.SignIn();
        var server = new RotatingServer { Clock = env.Clock };
        var header = await Manager(env, server).GetAsync("at-1", CancellationToken.None);
        Assert.Equal("at-2", header.Value);
    }

    [Fact]
    public async Task A_revoked_session_clears_the_store_and_exits_3()
    {
        using var env = new TestEnv(r => FakeServer.Problem(HttpStatusCode.Unauthorized, "unauthorized"));
        env.SignIn(accessLife: TimeSpan.FromSeconds(1));
        env.Clock.Now = TestEnv.Start.AddMinutes(5);
        var exit = await env.Run("whoami");
        Assert.Equal(3, exit);
        Assert.Equal(0, env.Store.Count);
        Assert.Contains("zaagl login", env.Console.Stderr);
    }

    [Fact]
    public async Task A_401_on_a_request_triggers_one_refresh_and_one_resend()
    {
        var links = 0;
        using var env = new TestEnv(r =>
        {
            if (r.Path == "/api/auth/refresh") return FakeServer.Json(HttpStatusCode.OK, Wire.Auth("at-2", "rt-2"));
            return ++links == 1
                ? FakeServer.Problem(HttpStatusCode.Unauthorized, "unauthorized")
                : FakeServer.Json(HttpStatusCode.Created, "{\"id\":\"44444444-4444-4444-4444-444444444444\",\"slug\":\"x1x1\",\"isActive\":true,\"createdAt\":\"2026-11-10T12:00:00Z\",\"shortUrl\":\"https://zaa.gl/x1x1\"}");
        });
        env.SignIn();
        Assert.Equal(0, await env.Run("link", "https://example.com"));
        Assert.Equal(new[] { "Bearer at-1", "Bearer at-2" },
            env.Server.Requests.Where(r => r.Path == "/api/links").Select(r => r.Headers["Authorization"]).ToArray());
        Assert.Equal(1, env.Server.Requests.Count(r => r.Path == "/api/auth/refresh"));
    }

    [Fact]
    public async Task A_stale_401_with_an_already_rotated_token_does_not_present_the_old_refresh_token_again()
    {
        // Another process refreshed (store holds at-2/rt-2) while this one was rejected on at-1: it must adopt at-2.
        using var env = new TestEnv();
        env.SignIn(access: "at-2", refresh: "rt-2");
        var server = new RotatingServer { Clock = env.Clock, CurrentRefresh = "rt-2" };
        var header = await Manager(env, server).GetAsync("at-1", CancellationToken.None);
        Assert.Equal("at-2", header.Value);
        Assert.Equal(0, server.Rotations);
    }
}

public class StoreAndWireTests
{
    [Fact]
    public void Stored_credential_round_trips_and_fits_the_windows_blob_limit_even_with_long_tokens()
    {
        var big = new string('a', 1500);
        var cred = new StoredCredential(big, DateTimeOffset.UnixEpoch, big, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1),
            Guid.NewGuid(), Guid.NewGuid(), "me@example.com", "Me");
        var bytes = cred.Serialize();
        Assert.True(bytes.Length <= StoredCredential.MaxBlobBytes);
        Assert.Equal(big, StoredCredential.Deserialize(bytes)!.RefreshToken);
        Assert.Null(StoredCredential.Deserialize("not json"u8.ToArray()));
    }

    [Fact]
    public void Store_key_is_per_api_host()
    {
        Assert.Equal("zaa.gl/cli/api.zaa.gl", CredentialStores.KeyFor(new Uri("https://api.zaa.gl/")));
        Assert.Equal("zaa.gl/cli/localhost:5080", CredentialStores.KeyFor(new Uri("http://localhost:5080/")));
    }

    [Fact]
    public void Device_flow_constants_match_the_contract_C9_2()
    {
        Assert.Equal("zaagl-cli", DeviceFlowWire.ClientId);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", DeviceFlowWire.GrantType);
        Assert.Equal(5, DeviceFlowWire.SlowDownIncrementSeconds);
    }

    [Fact]
    public async Task The_device_request_bodies_use_the_snake_case_wire_names()
    {
        using var env = new TestEnv(r => r.Path == "/api/auth/device/code"
            ? FakeServer.Json(HttpStatusCode.OK, Wire.DeviceCode)
            : FakeServer.Json(HttpStatusCode.OK, Wire.Auth("a", "r")));
        await env.Run("login", "--no-browser");
        using var code = JsonDocument.Parse(env.Server.Requests[0].BodyText);
        Assert.Equal("zaagl-cli", code.RootElement.GetProperty("client_id").GetString());
        Assert.True(code.RootElement.GetProperty("device_name").GetString()!.Length <= 64);
        Assert.True(code.RootElement.GetProperty("os").GetString()!.Length <= 32);
        using var token = JsonDocument.Parse(env.Server.Requests[1].BodyText);
        Assert.Equal(DeviceFlowWire.GrantType, token.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("dc-secret", token.RootElement.GetProperty("device_code").GetString());
    }

    [Theory]
    [InlineData("unauthorized", 401, 3)]
    [InlineData("insufficient_scope", 403, 3)]
    [InlineData("invalid-code", 401, 3)]
    [InlineData("plan_required", 402, 4)]
    [InlineData("quota-exceeded", 403, 4)]
    [InlineData("expiry-beyond-plan", 400, 4)]
    [InlineData("not_found", 404, 5)]
    [InlineData("expired", 410, 5)]
    [InlineData("service-unavailable", 503, 6)]
    [InlineData("too-many-requests", 429, 7)]
    [InlineData("validation", 400, 8)]
    [InlineData("snippet-too-large", 413, 8)]
    [InlineData("something-new", 500, 6)]
    [InlineData("something-new", 418, 1)]
    public void Problem_codes_map_to_the_documented_exit_codes(string code, int status, int expected) =>
        Assert.Equal(expected, ErrorMapper.ExitCodeFor(code, status));

    [Fact]
    public void Server_text_is_sanitised_before_it_reaches_the_terminal()
    {
        var ex = ErrorMapper.ToException(new ProblemDto(null, null, 400, "validation", "bad\u001b[31m input‮"), 400, null);
        Assert.DoesNotContain('\u001b', ex.Message);
        Assert.DoesNotContain('‮', ex.Message);
    }

    [Fact]
    public void Text_decoder_and_ansi_stripper_basics()
    {
        Assert.Equal("a€b", InputDecoder.Decode("a€b"u8).Text);
        Assert.Equal("x", AnsiStripper.Strip("\u001b[1;32mx\u001b[0m"));
        Assert.True(ExpiryParser.TryParseDuration("2w", out var d) && d == TimeSpan.FromDays(14));
    }
}

public class WindowsCredentialManagerTests
{
    [Fact]
    public void Real_credential_manager_round_trip_with_a_throwaway_key()
    {
        if (!OperatingSystem.IsWindows()) return; // per-OS integration runs on its own runner
        var store = new WindowsCredentialStore();
        var key = "zaa.gl/cli-test/" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(store.Read(key));
            var blob = new StoredCredential("rt-test", null, "at-test", null, null, null, null, "t@example.com", null).Serialize();
            store.Write(key, blob);
            Assert.Equal(blob, store.Read(key));
            store.Write(key, [1, 2, 3]); // overwrite
            Assert.Equal(new byte[] { 1, 2, 3 }, store.Read(key));
            Assert.True(store.Delete(key));
            Assert.Null(store.Read(key));
            Assert.False(store.Delete(key));
        }
        finally
        {
            store.Delete(key);
        }
    }
}
