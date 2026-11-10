using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Resources.Application;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Api.Tests;

/// <summary>The API for slice S2: kind=file, guests, three guest creates per hour (so the 429 is cheap to reach), small burn limits.</summary>
public class FilesS2Factory : FilesApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        base.ExtraSettings().Concat(
        [
            new KeyValuePair<string, string>("Uploads:Kinds", "media,file"),
            new KeyValuePair<string, string>("Uploads:GuestsEnabled", "true"),
            new KeyValuePair<string, string>("Guests:Enabled", "true"),
            new KeyValuePair<string, string>("Guests:PerMinute", "1000"),
            new KeyValuePair<string, string>("Guests:ContentPerHour", "4"),
            new KeyValuePair<string, string>("Guests:ContentPerDay", "40"),
        ]);
}

/// <summary>Shared helpers of the S2 suites (tus, simple, scan, tickets, SQL).</summary>
public abstract class S2Support(FilesS2Factory api)
{
    protected static readonly JsonSerializerOptions Json = TestPrincipals.Json;
    protected TestPrincipals P => api.Principals;

    protected static string B64(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v));

    /// <summary>Not a PNG, not an executable: plain bytes (a file).</summary>
    protected static byte[] Bytes(int size, byte seed = 1)
    {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(0x30 + (seed + i) % 40);
        return b;
    }

    protected static byte[] Png(int size)
    {
        var b = Bytes(size);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        return b;
    }

    protected static async Task<JsonElement> BodyAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    protected static string CodeProp(JsonElement b) => b.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";

    protected static HttpRequestMessage Create(long length, string kind, string filename, bool burn = false, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/uploads");
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Length", length.ToString());
        var meta = new List<string> { $"kind {B64(kind)}", $"filename {B64(filename)}" };
        if (burn) meta.Add($"burn {B64("1")}");
        request.Headers.Add("Upload-Metadata", string.Join(',', meta));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    protected static HttpRequestMessage Patch(Guid id, long offset, byte[] data)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/uploads/{id}") { Content = new ByteArrayContent(data) };
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Offset", offset.ToString());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        return request;
    }

    protected static HttpRequestMessage Complete(Guid id, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/uploads/{id}/complete") { Content = JsonContent.Create(body ?? new { @public = true }, options: Json) };
        request.Headers.Add("Idempotency-Key", "c-" + TestIps.UniqueSuffix());
        return request;
    }

    /// <summary>tus create, one PATCH, complete. Returns the status of the failing step, or 202 and the /complete body.</summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body, Guid UploadId)> UploadAsync(
        HttpClient client, byte[] data, string kind = "file", string filename = "doc.bin", object? completeBody = null, bool burn = false)
    {
        using var created = await client.SendAsync(Create(data.Length, kind, filename, burn));
        if (created.StatusCode != HttpStatusCode.Created) return (created.StatusCode, await BodyAsync(created), default);
        var id = Guid.Parse(created.Headers.Location!.OriginalString["/api/uploads/".Length..]);
        using var patched = await client.SendAsync(Patch(id, 0, data));
        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        using var completed = await client.SendAsync(Complete(id, completeBody));
        return (completed.StatusCode, await BodyAsync(completed), id);
    }

    protected async Task<Exception?> ScanAsync(Guid resourceId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.DedupeKey == $"scan:{resourceId}" && t.Status < QueuedTaskStatus.Done);
        try
        {
            await scope.ServiceProvider.GetRequiredService<ScanTaskHandler>()
                .HandleAsync(new ClaimedTask(row.Id, row.Kind, row.DedupeKey, row.Payload, 1, row.MaxAttempts), CancellationToken.None);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    /// <summary>Runs the pending <c>delete-bytes</c> task of one resource the way the worker would.</summary>
    protected async Task RunDeleteBytesAsync(Guid resourceId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var objectId = await files.StorageObjects.AsNoTracking().Where(o => o.ResourceId == resourceId).Select(o => (Guid?)o.Id).FirstOrDefaultAsync();
        if (objectId is null) return;
        var row = await files.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.DedupeKey == $"delete:{objectId}" && t.Status < QueuedTaskStatus.Done);
        Assert.NotNull(row);
        await ActivatorUtilities.CreateInstance<DeleteBytesTaskHandler>(scope.ServiceProvider)
            .HandleAsync(new ClaimedTask(row!.Id, row.Kind, row.DedupeKey, row.Payload, 1, row.MaxAttempts), CancellationToken.None);
    }

    protected async Task<T> WithLifecycleAsync<T>(Func<ResourceLifecycle, Task<T>> action)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ResourceLifecycle>());
    }

    protected HttpClient Public(string? linkKey = null)
    {
        var client = api.ApiClient();
        if (linkKey is not null) client.DefaultRequestHeaders.Add("X-Zaagl-Link-Key", linkKey);
        return client;
    }

    protected async Task<string> MintAsync(string code, string? linkKey = null)
    {
        using var client = Public(linkKey);
        using var response = await client.PostAsync($"/api/public/content/{code}/download", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"mint -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await BodyAsync(response)).GetProperty("url").GetString()!;
    }

    protected async Task<HttpResponseMessage> FetchAsync(string ticketUrl, string? range = null, HttpMethod? method = null)
    {
        using var client = api.HostClient(FilesApiFactory.UserContentHost);
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, "/d/" + ticketUrl[(ticketUrl.IndexOf("/d/", StringComparison.Ordinal) + 3)..]);
        if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
    }

    protected async Task<HttpStatusCode> StatusOfFetchAsync(string ticketUrl, string? range = null)
    {
        using var response = await FetchAsync(ticketUrl, range);
        return response.StatusCode;
    }

    protected static async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestDb.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    protected static Task ExecAsync(string sql, params (string Name, object Value)[] parameters) => ScalarAsync<int>(sql + "; SELECT 0", parameters);

    protected string AreaPath(StorageArea area, Guid id) =>
        Path.Combine(api.StorageRoot, StorageKeys.Create(area, id).Replace('/', Path.DirectorySeparatorChar));

    protected async Task<Guid> ObjectIdAsync(Guid resourceId) =>
        await ScalarAsync<Guid>("SELECT id FROM files.storage_objects WHERE resource_id = @r", ("r", resourceId));

    protected static readonly object FileBody = new { @public = true };
    protected static readonly object BurnBody = new { @public = true, burnAfterDownload = true };

    /// <summary>Uploads a scanned, ready file and returns its resource id and code.</summary>
    protected async Task<(Guid ResourceId, string Code)> ReadyFileAsync(HttpClient client, byte[] data, bool burn = false, string name = "doc.bin")
    {
        var (status, body, _) = await UploadAsync(client, data, "file", name, burn ? BurnBody : FileBody, burn);
        Assert.True(status == HttpStatusCode.Accepted, $"upload -> {status} {body}");
        var resourceId = body.GetProperty("resourceId").GetGuid();
        Assert.Null(await ScanAsync(resourceId));
        return (resourceId, body.GetProperty("code").GetString()!);
    }
}

/// <summary>QA-030: the burn claim is exactly once under parallel requests, and the other burn rules of ARC-019 section 5.</summary>
[Trait("Area", "BE-018-QA-030")]
public sealed class BurnClaimTests(FilesS2Factory api) : S2Support(api), IClassFixture<FilesS2Factory>
{
    [Fact]
    public async Task QA030_exactly_one_of_many_parallel_first_gets_wins_repeated()
    {
        var owner = await P.CreateUserAsync(starter: true, "race");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        const int rounds = 15, parallel = 40;
        for (var round = 0; round < rounds; round++)
        {
            var (resourceId, code) = await ReadyFileAsync(client, Bytes(3000, (byte)round), burn: true);
            var tickets = await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => MintAsync(code)));
            var statuses = await Task.WhenAll(tickets.Select(t => StatusOfFetchAsync(t)));
            Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
            Assert.Equal(parallel - 1, statuses.Count(s => s == HttpStatusCode.Gone));
            Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM resources.download_grants WHERE resource_id = @r AND is_burn_claim", ("r", resourceId)));
            Assert.Equal(1L, await ScalarAsync<long>("SELECT download_count FROM resources.resources WHERE id = @r", ("r", resourceId)));
        }
    }

    [Fact]
    public async Task The_same_ticket_twice_at_once_serves_both_and_the_17th_request_is_refused()
    {
        var owner = await P.CreateUserAsync(starter: true, "same");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (resourceId, code) = await ReadyFileAsync(client, Bytes(3000), burn: true);
        var ticket = await MintAsync(code);

        var first = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => StatusOfFetchAsync(ticket)));
        Assert.All(first, s => Assert.Equal(HttpStatusCode.OK, s)); // one claim, seven continuations (P2)
        Assert.Equal(8, await ScalarAsync<int>("SELECT request_count FROM resources.download_grants WHERE resource_id = @r AND is_burn_claim", ("r", resourceId)));
        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(ticket)); // 16 in total
        Assert.Equal(HttpStatusCode.Gone, await StatusOfFetchAsync(ticket));                          // the 17th

        // a second visitor cannot start: minting is refused after the claim
        using var visitor = Public();
        using var mint = await visitor.PostAsync($"/api/public/content/{code}/download", null);
        Assert.Equal(HttpStatusCode.Gone, mint.StatusCode);
        Assert.Equal("burned", (await BodyAsync(mint)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Head_a_refused_range_and_the_descriptor_never_claim_and_a_missing_object_does_not_burn()
    {
        var owner = await P.CreateUserAsync(starter: true, "noclaim");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (resourceId, code) = await ReadyFileAsync(client, Bytes((int)FilesApiFactory.ResponseCap + 50_000), burn: true);
        async Task<bool> Claimed() => await ScalarAsync<bool>("SELECT burn_claimed_at IS NOT NULL FROM resources.resources WHERE id = @r", ("r", resourceId));

        using (var descriptor = await Public().GetAsync($"/api/public/content/{code}"))
        {
            Assert.Equal(HttpStatusCode.OK, descriptor.StatusCode);
            var d = await BodyAsync(descriptor);
            Assert.Equal("file", d.GetProperty("kind").GetString());
            Assert.True(d.GetProperty("burnAfterDownload").GetBoolean());
            Assert.Equal(JsonValueKind.Null, d.GetProperty("inlineUrl").ValueKind);
            Assert.Equal(FilesApiFactory.ResponseCap, d.GetProperty("file").GetProperty("singleResponseLimitBytes").GetInt64());
            Assert.DoesNotContain("/d/", d.ToString());
        }
        var ticket = await MintAsync(code);
        using (var head = await FetchAsync(ticket, method: HttpMethod.Head)) Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.False(await Claimed());
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, await StatusOfFetchAsync(ticket)); // too big for one response, no Range: refused
        Assert.False(await Claimed());                                                               // a refused request consumes nothing
        Assert.Equal(0, await ScalarAsync<int>("SELECT request_count FROM resources.download_grants WHERE resource_id = @r", ("r", resourceId)));
        var part = await StatusOfFetchAsync(ticket, "bytes=0-99999");
        Assert.True(part is HttpStatusCode.OK or HttpStatusCode.PartialContent, part.ToString());
        Assert.True(await Claimed());

        // a missing object must not burn: remove the bytes of a second file, then try
        var (secondId, secondCode) = await ReadyFileAsync(client, Bytes(3000, 9), burn: true);
        var secondPath = AreaPath(StorageArea.Objects, await ObjectIdAsync(secondId));
        File.SetAttributes(secondPath, FileAttributes.Normal); // stored objects are read-only
        File.Delete(secondPath);
        Assert.Equal(HttpStatusCode.Gone, await StatusOfFetchAsync(await MintAsync(secondCode)));
        Assert.False(await ScalarAsync<bool>("SELECT burn_claimed_at IS NOT NULL FROM resources.resources WHERE id = @r", ("r", secondId)));
    }

    [Fact]
    public async Task A_plain_download_is_counted_once_per_ticket_and_reused_within_its_window()
    {
        var owner = await P.CreateUserAsync(starter: false, "count");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (resourceId, code) = await ReadyFileAsync(client, Bytes(3000));
        var t1 = await MintAsync(code);
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(t1));
        Assert.Equal(HttpStatusCode.PartialContent, await StatusOfFetchAsync(t1, "bytes=0-9"));
        using (var head = await FetchAsync(t1, method: HttpMethod.Head)) Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT download_count FROM resources.resources WHERE id = @r", ("r", resourceId)));
        Assert.Equal(2, await ScalarAsync<int>("SELECT request_count FROM resources.download_grants WHERE resource_id = @r AND purpose = 1", ("r", resourceId)));
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(await MintAsync(code)));
        using var detail = await client.GetAsync($"/api/resources?kind=file");
        var item = (await BodyAsync(detail)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == resourceId);
        Assert.Equal(2, item.GetProperty("downloadCount").GetInt64());

        // an owner ticket never counts
        using var ownerTicket = await client.PostAsync($"/api/resources/{resourceId}/download", null);
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync((await BodyAsync(ownerTicket)).GetProperty("url").GetString()!));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT download_count FROM resources.resources WHERE id = @r", ("r", resourceId)));
    }

    [Fact]
    public async Task Burn_is_gated_by_plan_kind_and_claim()
    {
        var free = await P.CreateUserAsync(starter: false, "burnfree");
        var starter = await P.CreateUserAsync(starter: true, "burnstarter");
        using var freeClient = api.ApiClient(bearer: free.AccessToken);
        using var starterClient = api.ApiClient(bearer: starter.AccessToken);

        // refused at tus create, before any byte (402 without the capability, 400 for media)
        using (var r = await freeClient.SendAsync(Create(100, "file", "a.bin", burn: true))) Assert.Equal(HttpStatusCode.PaymentRequired, r.StatusCode);
        using (var r = await starterClient.SendAsync(Create(100, "media", "a.png", burn: true))) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // and at /complete
        var (status, _, _) = await UploadAsync(freeClient, Bytes(500), "file", "a.bin", BurnBody);
        Assert.Equal(HttpStatusCode.PaymentRequired, status);

        // PATCH: toggle before a claim, never after
        var (resourceId, code) = await ReadyFileAsync(starterClient, Bytes(2000));
        using (var on = await starterClient.PatchAsJsonAsync($"/api/resources/{resourceId}", new { burnAfterDownload = true }, Json))
            Assert.True((await BodyAsync(on)).GetProperty("burnAfterDownload").GetBoolean());
        using (var off = await starterClient.PatchAsJsonAsync($"/api/resources/{resourceId}", new { burnAfterDownload = false }, Json))
            Assert.False((await BodyAsync(off)).GetProperty("burnAfterDownload").GetBoolean());
        (await starterClient.PatchAsJsonAsync($"/api/resources/{resourceId}", new { burnAfterDownload = true }, Json)).Dispose();
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(await MintAsync(code)));
        using var after = await starterClient.PatchAsJsonAsync($"/api/resources/{resourceId}", new { burnAfterDownload = false }, Json);
        Assert.Equal(HttpStatusCode.Conflict, after.StatusCode);
        Assert.Equal("burn-already-claimed", (await BodyAsync(after)).GetProperty("code").GetString());
        using var detail = await starterClient.GetAsync($"/api/resources/{resourceId}");
        var d = await BodyAsync(detail);
        Assert.Equal("burned", d.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, d.GetProperty("burnClaimedAt").ValueKind);
        using var publicDescriptor = await Public().GetAsync($"/api/public/content/{code}");
        Assert.Equal(HttpStatusCode.Gone, publicDescriptor.StatusCode);

        // the free user's storage view tells the uploader what the plan can do
        using var storage = await freeClient.GetAsync("/api/me/storage");
        var caps = (await BodyAsync(storage)).GetProperty("capabilities");
        Assert.False(caps.GetProperty("burnAfterDownload").GetBoolean());
        using var storage2 = await starterClient.GetAsync("/api/me/storage");
        Assert.True((await BodyAsync(storage2)).GetProperty("capabilities").GetProperty("burnAfterDownload").GetBoolean());
    }
}

/// <summary>The burn grace and the finaliser (the clock is moved once, at the end of the test).</summary>
[Trait("Area", "BE-018")]
public sealed class BurnGraceTests(FilesS2Factory api) : S2Support(api), IClassFixture<FilesS2Factory>
{
    [Fact]
    public async Task After_the_grace_the_claiming_ticket_is_refused_and_the_finaliser_deletes_the_bytes_once()
    {
        var owner = await P.CreateUserAsync(starter: true, "grace");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (resourceId, code) = await ReadyFileAsync(client, Bytes(4000), burn: true);
        var objectId = await ObjectIdAsync(resourceId);
        Assert.Equal(4000, await ScalarAsync<long>("SELECT used_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));

        var ticket = await MintAsync(code);
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(ticket));
        await WithLifecycleAsync(l => l.FinaliseBurnsAsync(CancellationToken.None)); // inside the grace: this resource is untouched
        Assert.Equal(1, await ScalarAsync<int>("SELECT bytes_state FROM resources.resources WHERE id = @r", ("r", resourceId)));

        api.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(ticket)); // a continuation inside the window
        api.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Gone, await StatusOfFetchAsync(ticket)); // the request path enforces the grace, before any job ran
        Assert.True(File.Exists(AreaPath(StorageArea.Objects, objectId)));

        await WithLifecycleAsync(l => l.FinaliseBurnsAsync(CancellationToken.None));
        Assert.Equal(3, await ScalarAsync<int>("SELECT bytes_state FROM resources.resources WHERE id = @r", ("r", resourceId)));
        await WithLifecycleAsync(l => l.FinaliseBurnsAsync(CancellationToken.None)); // idempotent: a second run changes nothing
        await RunDeleteBytesAsync(resourceId);
        Assert.False(File.Exists(AreaPath(StorageArea.Objects, objectId)));
        Assert.Equal(0, await ScalarAsync<long>("SELECT used_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
        Assert.Equal(3, await ScalarAsync<int>("SELECT bytes_state FROM resources.resources WHERE id = @r", ("r", resourceId)));
        using var descriptor = await Public().GetAsync($"/api/public/content/{code}");
        Assert.Equal(HttpStatusCode.Gone, descriptor.StatusCode);
        Assert.Equal("burned", (await BodyAsync(descriptor)).GetProperty("code").GetString());
    }
}

/// <summary>PD-16 interim type policy and re-kinding, and the simple upload (the policy is also unit tested in Zaagl.Files.Tests).</summary>
[Trait("Area", "BE-011-S2")]
public sealed class FileUploadTests(FilesS2Factory api) : S2Support(api), IClassFixture<FilesS2Factory>
{
    private static byte[] Exe()
    {
        var b = new byte[512];
        b[0] = (byte)'M'; b[1] = (byte)'Z'; b[0x3C] = 0x80;
        b[0x80] = (byte)'P'; b[0x81] = (byte)'E';
        return b;
    }

    private static byte[] EncryptedZip()
    {
        var b = new byte[256];
        new byte[] { 0x50, 0x4B, 0x03, 0x04, 20, 0, 0x01, 0x00 }.CopyTo(b, 0); // flag bit 0: encrypted
        return b;
    }

    [Fact]
    public async Task Executables_are_refused_for_free_and_guests_and_allowed_for_starter_and_encrypted_archives_for_nobody()
    {
        var free = await P.CreateUserAsync(starter: false, "exefree");
        var starter = await P.CreateUserAsync(starter: true, "exestarter");
        using var freeClient = api.ApiClient(bearer: free.AccessToken);
        using var starterClient = api.ApiClient(bearer: starter.AccessToken);

        var (status, body, _) = await UploadAsync(freeClient, Exe(), "file", "tool.dat");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("type-not-allowed", body.GetProperty("reason").GetString());
        (status, body, _) = await UploadAsync(freeClient, Bytes(300), "file", "setup.exe"); // a blocklisted extension on the display name
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        (status, _, _) = await UploadAsync(starterClient, Exe(), "file", "tool.dat");
        Assert.Equal(HttpStatusCode.Accepted, status);
        (status, _, _) = await UploadAsync(starterClient, EncryptedZip(), "file", "secret.zip");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);

        using var guest = api.ApiClient(TestIps.RandomV4());
        (status, _, _) = await UploadAsync(guest, Exe(), "file", "tool.dat", new { @public = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
    }

    [Fact]
    public async Task A_media_upload_that_is_not_an_image_becomes_a_file_and_a_declared_file_stays_one()
    {
        var owner = await P.CreateUserAsync(starter: false, "rekind");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (status, body, _) = await UploadAsync(client, Bytes(900), "media", "notes.txt");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = body.GetProperty("resourceId").GetGuid();
        Assert.Null(await ScanAsync(id));
        using var detail = await client.GetAsync($"/api/resources/{id}");
        var d = await BodyAsync(detail);
        Assert.Equal("file", d.GetProperty("kind").GetString());
        Assert.Equal("ready", d.GetProperty("state").GetString());

        var (_, pngBody, _) = await UploadAsync(client, Png(900), "file", "pic.png");
        var pngId = pngBody.GetProperty("resourceId").GetGuid();
        Assert.Null(await ScanAsync(pngId));
        using var pngDetail = await client.GetAsync($"/api/resources/{pngId}");
        Assert.Equal("file", (await BodyAsync(pngDetail)).GetProperty("kind").GetString());
    }

    private static MultipartFormDataContent Multipart(byte[] data, string kind, string filename, object? metadata = null, bool fileFirst = false)
    {
        var form = new MultipartFormDataContent("zaagl-boundary");
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (fileFirst) form.Add(file, "file", filename);
        form.Add(new StringContent(kind), "kind");
        if (metadata is not null)
        {
            var json = new StringContent(JsonSerializer.Serialize(metadata, Json), Encoding.UTF8, "application/json");
            form.Add(json, "metadata");
        }
        if (!fileFirst) form.Add(file, "file", filename);
        return form;
    }

    [Fact]
    public async Task Simple_upload_one_request_for_an_account_and_a_guest()
    {
        var owner = await P.CreateUserAsync(starter: true, "simple");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Bytes(7000);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/uploads/simple") { Content = Multipart(data, "file", "report.pdf", new { title = "Q3", burnAfterDownload = true, @public = true }) };
        request.Headers.Add("Idempotency-Key", "s-" + TestIps.UniqueSuffix());
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var body = await BodyAsync(response);
        var id = body.GetProperty("resourceId").GetGuid();
        Assert.Null(await ScanAsync(id));
        Assert.Equal(7000, await ScalarAsync<long>("SELECT used_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
        Assert.Equal(0, await ScalarAsync<long>("SELECT reserved_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
        using var detail = await client.GetAsync($"/api/resources/{id}");
        var d = await BodyAsync(detail);
        Assert.True(d.GetProperty("burnAfterDownload").GetBoolean());
        Assert.Equal("report.pdf", d.GetProperty("filename").GetString());
        Assert.Equal(7000, d.GetProperty("sizeBytes").GetInt64());

        // guests may use it too (same counters and rules as tus); the file part must come last, and Content-Length is required
        using var guest = api.ApiClient(TestIps.RandomV4());
        using var guestRequest = new HttpRequestMessage(HttpMethod.Post, "/api/uploads/simple") { Content = Multipart(Bytes(1200), "file", "g.bin", new { @public = false }) };
        guestRequest.Headers.Add("Idempotency-Key", "g-" + TestIps.UniqueSuffix());
        using var guestResponse = await guest.SendAsync(guestRequest);
        Assert.True(guestResponse.StatusCode == HttpStatusCode.Accepted, $"{(int)guestResponse.StatusCode} {await guestResponse.Content.ReadAsStringAsync()}");
        Assert.Equal(JsonValueKind.String, (await BodyAsync(guestResponse)).GetProperty("linkKey").ValueKind);

        using var misordered = new HttpRequestMessage(HttpMethod.Post, "/api/uploads/simple") { Content = Multipart(Bytes(100), "file", "x.bin", fileFirst: true) };
        misordered.Headers.Add("Idempotency-Key", "m-" + TestIps.UniqueSuffix());
        using var misorderedResponse = await client.SendAsync(misordered);
        Assert.Equal(HttpStatusCode.BadRequest, misorderedResponse.StatusCode);
        using var noKey = new HttpRequestMessage(HttpMethod.Post, "/api/uploads/simple") { Content = Multipart(Bytes(100), "file", "x.bin") };
        using var noKeyResponse = await client.SendAsync(noKey);
        Assert.Equal(HttpStatusCode.BadRequest, noKeyResponse.StatusCode);
    }
}

/// <summary>Guest uploads: no credential, tied to the guest principal, PD-14 counters (429 quota), no burn, no public.</summary>
[Trait("Area", "BE-011-S2")]
public sealed class GuestUploadTests(FilesS2Factory api) : S2Support(api), IClassFixture<FilesS2Factory>
{
    [Fact]
    public async Task A_guest_uploads_downloads_and_nobody_else_can_touch_the_session()
    {
        var ip = TestIps.RandomV4();
        using var guest = api.ApiClient(ip);
        var data = Bytes(2500);
        using var created = await guest.SendAsync(Create(data.Length, "file", "g.bin"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = Guid.Parse(created.Headers.Location!.OriginalString["/api/uploads/".Length..]);

        using var stranger = api.ApiClient(TestIps.RandomV4());
        using (var head = await stranger.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}"))) Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
        using (var patch = await stranger.SendAsync(Patch(id, 0, data))) Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        using (var delete = await stranger.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/uploads/{id}"))) Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        using (var head = await guest.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}"))) Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        using (var patched = await guest.SendAsync(Patch(id, 0, data))) Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        using (var publicRefused = await guest.SendAsync(Complete(id, new { @public = true })))
            Assert.Equal(HttpStatusCode.BadRequest, publicRefused.StatusCode); // guests choose a private link
        using (var burnRefused = await guest.SendAsync(Complete(id, new { burnAfterDownload = true })))
            Assert.Equal(HttpStatusCode.PaymentRequired, burnRefused.StatusCode); // the guest plan has no burn capability
        using var completed = await guest.SendAsync(Complete(id, new { @public = false }));
        Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode);
        var body = await BodyAsync(completed);
        var resourceId = body.GetProperty("resourceId").GetGuid();
        var linkKey = body.GetProperty("linkKey").GetString()!;
        Assert.Null(await ScanAsync(resourceId));

        using (var noKey = await Public().GetAsync($"/api/public/content/{body.GetProperty("code").GetString()}")) Assert.Equal(HttpStatusCode.NotFound, noKey.StatusCode);
        var ticket = await MintAsync(body.GetProperty("code").GetString()!, linkKey);
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(ticket));
    }

    [Fact]
    public async Task Guest_burn_is_a_402_and_counter_exhaustion_is_a_429_quota()
    {
        var ip = TestIps.RandomV4();
        using var guest = api.ApiClient(ip);
        using (var burn = await guest.SendAsync(Create(100, "file", "b.bin", burn: true))) Assert.Equal(HttpStatusCode.PaymentRequired, burn.StatusCode);
        var created = new List<Guid>();
        for (var i = 0; i < 4; i++) // Guests:ContentPerHour = 4 in this factory; the refused burn above was refunded (nothing counted)
        {
            using var ok = await guest.SendAsync(Create(100, "file", $"g{i}.bin"));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            created.Add(Guid.Parse(ok.Headers.Location!.OriginalString["/api/uploads/".Length..]));
        }
        using var limited = await guest.SendAsync(Create(100, "file", "g5.bin"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("quota", (await BodyAsync(limited)).GetProperty("code").GetString());
        foreach (var id in created) (await guest.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/uploads/{id}"))).Dispose();
    }

    [Fact]
    public async Task A_failing_credential_is_never_a_guest_upload()
    {
        using var bad = api.ApiClient(TestIps.RandomV4(), bearer: "not-a-token");
        using var response = await bad.SendAsync(Create(100, "file", "x.bin"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>C12 takedown, the C10 drain, the owner list summary, links ?kind= and the redirect (ARC-019 sections 8 and 11).</summary>
[Trait("Area", "BE-022")]
public sealed class ContentAbuseAndLibraryTests(FilesS2Factory api) : S2Support(api), IClassFixture<FilesS2Factory>
{
    [Fact]
    public async Task An_infected_file_takes_its_link_down_with_evidence_and_the_owner_sees_why()
    {
        var owner = await P.CreateUserAsync(starter: false, "abuse");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Bytes(2000).Concat(Encoding.ASCII.GetBytes(FakeClamdServer.Marker)).ToArray();
        var (status, body, _) = await UploadAsync(client, data, "file", "bad.bin");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var resourceId = body.GetProperty("resourceId").GetGuid();
        var code = body.GetProperty("code").GetString()!;
        Assert.Null(await ScanAsync(resourceId));

        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM links.abuse_evidence WHERE resource_id = @r AND disabled_kind = 'safety' AND reason LIKE '%sha256=%'", ("r", resourceId)));
        await using (var scope = api.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<LifecycleOutboxDrain>().DrainAsync(CancellationToken.None);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM links.lifecycle_outbox WHERE resource_id = @r AND processed_at IS NULL", ("r", resourceId)));

        using var list = await client.GetAsync("/api/resources?kind=file");
        var item = (await BodyAsync(list)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == resourceId);
        Assert.Equal("infected", item.GetProperty("rejectedReason").GetString()); // asked by the frontend: in the list summary
        using var viewer = await Public().GetAsync($"/api/public/content/{code}");
        Assert.Equal(HttpStatusCode.Gone, viewer.StatusCode);
        Assert.Equal("taken-down", (await BodyAsync(viewer)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Owner_delete_and_renewal_reach_the_resource_copies_through_the_outbox()
    {
        var owner = await P.CreateUserAsync(starter: true, "outbox");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (uploadStatus, uploaded, _) = await UploadAsync(client, Bytes(1500), "file", "o.bin", new { @public = true, expiresAt = DateTime.UtcNow.AddDays(5) });
        Assert.Equal(HttpStatusCode.Accepted, uploadStatus);
        var resourceId = uploaded.GetProperty("resourceId").GetGuid();
        Assert.Null(await ScanAsync(resourceId));
        var renewTo = DateTime.UtcNow.AddDays(20);
        using (var renewed = await client.PatchAsJsonAsync($"/api/resources/{resourceId}", new { expiresAt = renewTo }, Json)) Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        using (var deleted = await client.DeleteAsync($"/api/resources/{resourceId}")) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using (var scope = api.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<LifecycleOutboxDrain>().DrainAsync(CancellationToken.None) >= 2);
        Assert.True(await ScalarAsync<bool>("SELECT deleted_at IS NOT NULL FROM resources.resources WHERE id = @r", ("r", resourceId)));
        // the bytes stay until purge_after (PD-55), and the quota is still counted
        Assert.True(File.Exists(AreaPath(StorageArea.Objects, await ObjectIdAsync(resourceId))));
        Assert.True(await ScalarAsync<long>("SELECT used_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)) >= 1500);
    }

    [Fact]
    public async Task Links_list_carries_kind_and_resource_id_and_the_file_short_link_redirects_to_the_viewer()
    {
        var owner = await P.CreateUserAsync(starter: true, "kinds");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (resourceId, code) = await ReadyFileAsync(client, Bytes(900), burn: true);

        using var urlOnly = await client.GetAsync("/api/links");
        Assert.DoesNotContain(code, (await BodyAsync(urlOnly)).ToString()); // the default stays URL links (Phase 0 clients see no change)
        using var files = await client.GetAsync("/api/links?kind=file");
        var link = (await BodyAsync(files)).GetProperty("items").EnumerateArray().Single().GetProperty("link");
        Assert.Equal("file", link.GetProperty("kind").GetString());
        Assert.Equal(resourceId, link.GetProperty("resourceId").GetGuid());
        using var all = await client.GetAsync("/api/links?kind=all");
        Assert.Contains(code, (await BodyAsync(all)).ToString());

        using var shortClient = api.ShortClient();
        using var redirect = await shortClient.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Contains("/f/" + code, redirect.Headers.Location!.ToString());
        // a burned file still redirects to the viewer, which shows the state (ARC-019 section 8)
        Assert.Equal(HttpStatusCode.OK, await StatusOfFetchAsync(await MintAsync(code)));
        using var afterBurn = await shortClient.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.Redirect, afterBurn.StatusCode);
        Assert.Contains("/f/" + code, afterBurn.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Quota_reconciliation_repairs_drift_and_the_orphan_sweep_gives_the_reservation_back()
    {
        var owner = await P.CreateUserAsync(starter: true, "recon");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        await ReadyFileAsync(client, Bytes(1000));
        await ExecAsync("UPDATE resources.quota_ledger SET used_bytes = used_bytes + 777, reserved_bytes = reserved_bytes + 5 WHERE owner_id = @o", ("o", owner.Id));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ContentQuota>().ReconcileAsync(CancellationToken.None);
            Assert.True(result.Drifted >= 1);
        }
        Assert.Equal(1000, await ScalarAsync<long>("SELECT used_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
        Assert.Equal(0, await ScalarAsync<long>("SELECT reserved_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));

        // an upload that registered its resource but died before the link: reserved bytes, no link, older than a day
        using var created = await client.SendAsync(Create(3000, "file", "o.bin"));
        var uploadId = Guid.Parse(created.Headers.Location!.OriginalString["/api/uploads/".Length..]);
        (await client.SendAsync(Patch(uploadId, 0, Bytes(3000)))).Dispose();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Resources.Contracts.IResourceRegistry>();
            var reservation = await ScalarAsync<Guid>("SELECT reservation_id FROM files.upload_sessions WHERE id = @i", ("i", uploadId));
            var registered = await registry.RegisterAsync(new Zaagl.Modules.Resources.Contracts.RegisterResourceCommand(
                Zaagl.Modules.Resources.Contracts.ResourceKind.File, new(owner.Id, null), 3000, DateTime.UtcNow.AddDays(3), null, false,
                Zaagl.Modules.Resources.Contracts.CreatedVia.Web, reservation), CancellationToken.None);
            Assert.True(registered.IsSuccess);
            await ExecAsync("UPDATE resources.resources SET created_at = now() - interval '2 days' WHERE id = @r", ("r", registered.Value!.ResourceId));
            Assert.Equal(3000, await ScalarAsync<long>("SELECT reserved_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
            Assert.True(await WithLifecycleAsync(l => l.SweepOrphansAsync(CancellationToken.None)) >= 1);
            Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM resources.resources WHERE id = @r", ("r", registered.Value.ResourceId)));
        }
        Assert.Equal(0, await ScalarAsync<long>("SELECT reserved_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
    }
}

/// <summary>Expiry and purge coupled to the link (ARC-019 section 8). The clock is moved forward twice, at the end of one test.</summary>
[Trait("Area", "BE-019")]
public sealed class ExpiryAndPurgeTests(FilesS2Factory api) : S2Support(api), IClassFixture<FilesS2Factory>
{
    [Fact]
    public async Task Account_bytes_survive_expiry_until_purge_after_and_guest_bytes_go_at_expiry()
    {
        var owner = await P.CreateUserAsync(starter: false, "expiry");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (accountId, accountCode) = await ReadyFileAsync(client, Bytes(3500));
        var accountObject = await ObjectIdAsync(accountId);

        using var guest = api.ApiClient(TestIps.RandomV4());
        var (status, body, _) = await UploadAsync(guest, Bytes(1800), "file", "g.bin", new { @public = true }, false);
        Assert.Equal(HttpStatusCode.BadRequest, status); // guests cannot choose public
        (status, body, _) = await UploadAsync(guest, Bytes(1800), "file", "g.bin", new { @public = false });
        Assert.Equal(HttpStatusCode.Accepted, status);
        var guestId = body.GetProperty("resourceId").GetGuid();
        Assert.Null(await ScanAsync(guestId));
        var guestObject = await ObjectIdAsync(guestId);

        // day 8: both links expired (retention 7 days for Free and guests)
        api.Clock.Advance(TimeSpan.FromDays(8));
        using (var expired = await Public().GetAsync($"/api/public/content/{accountCode}"))
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        await WithLifecycleAsync(l => l.PurgeDueAsync(CancellationToken.None)); // the guest's purge_after is its expiry; the account's is 90 days later
        Assert.Equal(3, await ScalarAsync<int>("SELECT bytes_state FROM resources.resources WHERE id = @r", ("r", guestId)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT bytes_state FROM resources.resources WHERE id = @r", ("r", accountId)));
        await RunDeleteBytesAsync(guestId);
        Assert.False(File.Exists(AreaPath(StorageArea.Objects, guestObject)));
        Assert.True(File.Exists(AreaPath(StorageArea.Objects, accountObject)));                  // quarantined, kept (PD-06)
        Assert.Equal(3500, await ScalarAsync<long>("SELECT used_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
        await MintRefusedAsync(accountCode);

        // after purge_after (90 days of quarantine) the bytes go and the quota is released
        api.Clock.Advance(TimeSpan.FromDays(91));
        await WithLifecycleAsync(l => l.PurgeDueAsync(CancellationToken.None));
        Assert.Equal(3, await ScalarAsync<int>("SELECT bytes_state FROM resources.resources WHERE id = @r", ("r", accountId)));
        await RunDeleteBytesAsync(accountId);
        Assert.False(File.Exists(AreaPath(StorageArea.Objects, accountObject)));
        Assert.Equal(0, await ScalarAsync<long>("SELECT used_bytes FROM resources.quota_ledger WHERE owner_id = @o", ("o", owner.Id)));
        await WithLifecycleAsync(l => l.PurgeDueAsync(CancellationToken.None)); // idempotent
    }

    /// <summary>The download button refuses an expired link; asserts 410.</summary>
    private async Task MintRefusedAsync(string code)
    {
        using var response = await Public().PostAsync($"/api/public/content/{code}/download", null);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }
}
