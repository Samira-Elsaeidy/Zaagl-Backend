using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Application;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Api.Tests;

public sealed class FakeDiskProbe : IDiskSpaceProbe
{
    private const long Tb = 1L << 40;
    public volatile bool Full;
    public long Total = Tb;
    public long Free = Tb - (Tb / 10);

    public (long TotalBytes, long FreeBytes) Read(string path) => Full ? (Total, 5L * 1024 * 1024) : (Total, Free);
}

/// <summary>The API with a user-content host, ticket keys, a fake clamd, a temp storage root and the scan handler hosted in-process.</summary>
public class FilesApiFactory : ZaaglApiFactory
{
    public const string UserContentHost = "usercontent.zaa.test";
    public const long ResponseCap = 200_000;

    public FakeClamdServer Clamd { get; } = new();
    public FakeDiskProbe Disk { get; } = new();
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "zaagl-files-tests-" + Guid.NewGuid().ToString("N"));

    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
    [
        new("Hosts:UserContent", UserContentHost),
        new("Content:UserContentBaseUrl", $"http://{UserContentHost}"),
        new("Content:TicketKeys:0:Kid", "t1"),
        new("Content:TicketKeys:0:Key", Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("files-tests-ticket-key")))),
        new("Content:DirectObjectServing", "true"),
        new("Content:MaxResponseBytes", ResponseCap.ToString()),
        new("Content:AppOrigin", "https://app.zaa.test"),
        new("Hosts:RewriteDeliveryPath", "true"),
        new("RateLimits:ContentWritePerMinute", "100000"),
        new("RateLimits:ContentReadPerMinute", "100000"),
        new("Storage:Root", StorageRoot),
        new("Uploads:Kinds", "media"),
        new("Uploads:GuestsEnabled", "false"),
        new("Scan:Provider", "Clamd"),
        new("Scan:TimeoutSeconds", "2"),
        new("ClamAv:Host", "127.0.0.1"),
        new("ClamAv:Port", Clamd.Port.ToString()),
        new("ClamAv:ScanByPath", "false"),
    ];

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDiskSpaceProbe>();
            services.AddSingleton<IDiskSpaceProbe>(Disk);
            // The scan task runs in the worker in production; the tests host its handler in-process and run it on demand.
            services.AddSingleton<IMalwareScanner, ClamdScanner>();
            services.AddSingleton<ScanConcurrencyGate>();
            services.AddScoped<ITaskHandler<FilesDbContext>, ScanTaskHandler>();
            services.AddScoped<ScanTaskHandler>();
            services.AddScoped<UploadSessionExpiry>();
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Clamd.DisposeAsync();
        try { Directory.Delete(StorageRoot, recursive: true); } catch (Exception) { }
    }
}

/// <summary>
/// BE-011, BE-012, BE-014, BE-017 (E1.2 slice S1, ARC-009, ARC-008, ARC-006): plain image upload through tus, /complete, the scan task,
/// the tickets and the owner API, over HTTP on the real Program and the real database, with a fake clamd.
/// </summary>
[Trait("Area", "BE-011-BE-017")]
public sealed class FilesApiTests(FilesApiFactory api) : IClassFixture<FilesApiFactory>
{
    private static readonly JsonSerializerOptions Json = TestPrincipals.Json;
    private TestPrincipals P => api.Principals;

    // ---- helpers ------------------------------------------------------------------------------------

    private static byte[] Png(int size, byte fill = 7)
    {
        var bytes = new byte[size];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        for (var i = 8; i < size; i++) bytes[i] = (byte)(fill + i % 13);
        return bytes;
    }

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static HttpRequestMessage CreateRequest(long? length, string? kind = "media", string? filename = "pic.png", string? tus = "1.0.0", string? idempotencyKey = null, bool defer = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/uploads");
        if (tus is not null) request.Headers.Add("Tus-Resumable", tus);
        if (length is not null) request.Headers.Add("Upload-Length", length.Value.ToString());
        if (defer) request.Headers.Add("Upload-Defer-Length", "1");
        var metadata = new List<string>();
        if (kind is not null) metadata.Add($"kind {B64(kind)}");
        if (filename is not null) metadata.Add($"filename {B64(filename)}");
        metadata.Add($"filetype {B64("image/png")}");
        request.Headers.Add("Upload-Metadata", string.Join(',', metadata));
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static HttpRequestMessage PatchRequest(Guid id, long offset, byte[] data, string contentType = "application/offset+octet-stream")
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/uploads/{id}") { Content = new ByteArrayContent(data) };
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Offset", offset.ToString());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return request;
    }

    private static Guid IdFromLocation(HttpResponseMessage response) =>
        Guid.Parse(response.Headers.Location!.OriginalString["/api/uploads/".Length..]);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string CodeOf(JsonElement body) => body.GetProperty("code").GetString()!;
    private static Guid ResourceIdOf(JsonElement body) => body.GetProperty("resourceId").GetGuid();

    private async Task<Guid> CreateSessionAsync(HttpClient client, long length)
    {
        using var created = await client.SendAsync(CreateRequest(length));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return IdFromLocation(created);
    }

    private static async Task PatchAllAsync(HttpClient client, Guid id, byte[] data, int chunk = 0)
    {
        chunk = chunk == 0 ? data.Length : chunk;
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            using var response = await client.SendAsync(PatchRequest(id, offset, data[offset..Math.Min(data.Length, offset + chunk)]));
            Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"PATCH at {offset} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            Assert.Equal(Math.Min(data.Length, offset + chunk).ToString(), response.Headers.GetValues("Upload-Offset").Single());
        }
    }

    private static HttpRequestMessage CompleteRequest(Guid id, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/uploads/{id}/complete")
        {
            Content = JsonContent.Create(body ?? new { @public = true }, options: Json),
        };
        request.Headers.Add("Idempotency-Key", key ?? "c-" + TestIps.UniqueSuffix());
        return request;
    }

    /// <summary>create, upload, complete (public link). Returns the session id and the /complete body.</summary>
    private async Task<(Guid UploadId, JsonElement Completed)> UploadAsync(HttpClient client, byte[] data, object? completeBody = null, int chunk = 0)
    {
        var id = await CreateSessionAsync(client, data.Length);
        await PatchAllAsync(client, id, data, chunk);
        using var completed = await client.SendAsync(CompleteRequest(id, completeBody));
        Assert.True(completed.StatusCode == HttpStatusCode.Accepted, $"/complete -> {(int)completed.StatusCode} {await completed.Content.ReadAsStringAsync()}");
        return (id, await BodyAsync(completed));
    }

    /// <summary>Runs the scan task of one resource the way the worker would (claim the row, run the handler).</summary>
    private async Task<Exception?> RunScanAsync(Guid resourceId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.DedupeKey == $"scan:{resourceId}" && t.Status < QueuedTaskStatus.Done);
        var handler = scope.ServiceProvider.GetRequiredService<ScanTaskHandler>();
        try
        {
            await handler.HandleAsync(new ClaimedTask(row.Id, row.Kind, row.DedupeKey, row.Payload, 1, row.MaxAttempts), CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task KillScanAsync(Guid resourceId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.DedupeKey == $"scan:{resourceId}" && t.Status < QueuedTaskStatus.Done);
        await scope.ServiceProvider.GetRequiredService<ScanTaskHandler>()
            .OnDeadAsync(new ClaimedTask(row.Id, row.Kind, row.DedupeKey, row.Payload, 6, 6), CancellationToken.None);
    }

    private HttpClient Public(string? linkKey = null)
    {
        var client = api.ApiClient();
        if (linkKey is not null) client.DefaultRequestHeaders.Add("X-Zaagl-Link-Key", linkKey);
        return client;
    }

    private static string TicketOf(string url) => url[(url.IndexOf("/d/", StringComparison.Ordinal) + 3)..];

    private async Task<HttpResponseMessage> FetchAsync(string ticketUrl, string? range = null, HttpMethod? method = null)
    {
        using var client = api.HostClient(FilesApiFactory.UserContentHost);
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, "/d/" + TicketOf(ticketUrl));
        if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private async Task<(JsonElement Status, JsonElement Storage)> OwnerViewAsync(TestUser owner, Guid resourceId)
    {
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var status = await client.GetAsync($"/api/resources/{resourceId}/status");
        using var storage = await client.GetAsync("/api/me/storage");
        return (await BodyAsync(status), await BodyAsync(storage));
    }

    private async Task<JsonElement> StorageAsync(TestUser owner)
    {
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var response = await client.GetAsync("/api/me/storage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    private string AreaPath(StorageArea area, Guid id) =>
        Path.Combine(api.StorageRoot, StorageKeys.Create(area, id).Replace('/', Path.DirectorySeparatorChar));

    private static async Task<int> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestDb.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    // ---- the whole path -----------------------------------------------------------------------------

    [Fact]
    public async Task A_png_goes_through_tus_complete_scan_and_is_served_byte_for_byte()
    {
        var owner = await P.CreateUserAsync(starter: false, "up");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(150_000);
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));

        // create: the quota is reserved at once
        var id = await CreateSessionAsync(client, data.Length);
        var afterCreate = await StorageAsync(owner);
        Assert.Equal(data.Length, afterCreate.GetProperty("reservedBytes").GetInt64());
        Assert.Equal(1, afterCreate.GetProperty("openUploads").GetInt32());

        // HEAD, PATCH in two chunks, HEAD
        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}")))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal("0", head.Headers.GetValues("Upload-Offset").Single());
            Assert.Equal(data.Length.ToString(), head.Headers.GetValues("Upload-Length").Single());
            Assert.True(head.Headers.CacheControl!.NoStore);
            Assert.Equal("1.0.0", head.Headers.GetValues("Tus-Resumable").Single());
        }
        await PatchAllAsync(client, id, data, chunk: 100_000);

        // complete: 202 processing, link at once, the one-time key (keyed link)
        using var completeResponse = await client.SendAsync(CompleteRequest(id, new { title = "A picture", @public = false }));
        Assert.Equal(HttpStatusCode.Accepted, completeResponse.StatusCode);
        var completed = await BodyAsync(completeResponse);
        Assert.Equal("processing", completed.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(completed.GetProperty("linkKey").GetString()));
        var resourceId = ResourceIdOf(completed);
        var code = CodeOf(completed);
        var linkKey = completed.GetProperty("linkKey").GetString()!;

        // while processing: the visitor sees "processing" and no URL; the owner status says so with Retry-After
        using (var visitor = await Public(linkKey).GetAsync($"/api/public/content/{code}"))
        {
            Assert.Equal(HttpStatusCode.OK, visitor.StatusCode);
            var descriptor = await BodyAsync(visitor);
            Assert.Equal("media", descriptor.GetProperty("kind").GetString());
            Assert.Equal("processing", descriptor.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, descriptor.GetProperty("inlineUrl").ValueKind);
        }
        using (var status = await client.GetAsync($"/api/resources/{resourceId}/status"))
        {
            var body = await BodyAsync(status);
            Assert.Equal("processing", body.GetProperty("state").GetString());
            Assert.True(status.Headers.Contains("Retry-After"));
        }
        // the quota is counted once, not twice (F-9): reserved is still the declared length
        Assert.Equal(data.Length, (await StorageAsync(owner)).GetProperty("reservedBytes").GetInt64());
        Assert.Equal(0, (await StorageAsync(owner)).GetProperty("usedBytes").GetInt64());
        Assert.True(File.Exists(AreaPath(StorageArea.Temp, id)));

        // the worker scans, promotes (same id, rename), marks ready
        Assert.Null(await RunScanAsync(resourceId));
        Assert.False(File.Exists(AreaPath(StorageArea.Temp, id)));
        Assert.True(File.Exists(AreaPath(StorageArea.Objects, id)));
        var storage = await StorageAsync(owner);
        Assert.Equal(0, storage.GetProperty("reservedBytes").GetInt64());
        Assert.Equal(data.Length, storage.GetProperty("usedBytes").GetInt64());
        Assert.Equal(0, storage.GetProperty("openUploads").GetInt32()); // the session is Finalized, not open

        // the visitor now gets the image: inlineUrl is an Inline ticket, never a download URL
        using var ready = await Public(linkKey).GetAsync($"/api/public/content/{code}");
        var readyBody = await BodyAsync(ready);
        Assert.Equal("ready", readyBody.GetProperty("state").GetString());
        Assert.Equal("image/png", readyBody.GetProperty("mime").GetString());
        Assert.Equal("A picture", readyBody.GetProperty("title").GetString());
        Assert.Equal("pic.png", readyBody.GetProperty("filename").GetString());
        Assert.Equal("image", readyBody.GetProperty("media").GetProperty("mediaType").GetString());
        Assert.True(readyBody.GetProperty("media").GetProperty("browserPlayable").GetBoolean());
        var inlineUrl = readyBody.GetProperty("inlineUrl").GetString()!;
        Assert.Contains("/d/", inlineUrl);

        using var served = await FetchAsync(inlineUrl);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var bytes = await served.Content.ReadAsByteArrayAsync();
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(bytes))); // sha256(served) == sha256(uploaded)
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline;", served.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", served.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("https://app.zaa.test", served.Headers.GetValues("X-Zaagl-Acao").Single());
        Assert.False(served.Headers.Contains("Set-Cookie"));

        // the owner detail carries the hash of the stored bytes and the scan result
        using var detail = await client.GetAsync($"/api/resources/{resourceId}");
        var d = await BodyAsync(detail);
        Assert.Equal(sha, d.GetProperty("file").GetProperty("sha256").GetString());
        Assert.Equal("clean", d.GetProperty("scan").GetProperty("status").GetString());
        Assert.Equal("ready", d.GetProperty("state").GetString());
        Assert.Equal(data.Length, d.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("thumbnailUrl").ValueKind);
    }

    [Fact]
    public async Task Eicar_style_content_is_rejected_quarantined_and_never_served()
    {
        var owner = await P.CreateUserAsync(starter: false, "eicar");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        // A real PNG header (so the sniff accepts it) followed by the test malware string: the scan is what catches it.
        var data = Png(5000).Concat(Encoding.ASCII.GetBytes(FakeClamdServer.Marker)).ToArray();
        var (uploadId, completed) = await UploadAsync(client, data);
        var resourceId = ResourceIdOf(completed);
        var code = CodeOf(completed);

        Assert.Null(await RunScanAsync(resourceId));

        // rejected as infected, bytes in quarantine (never in objects), quota released
        using var detail = await client.GetAsync($"/api/resources/{resourceId}");
        var d = await BodyAsync(detail);
        // S2 (BE-022, ILinkTakedown): the scan verdict also takes the link down (spec 7.15), so the computed state is disabled and the reason stays visible.
        Assert.Equal("disabled", d.GetProperty("state").GetString());
        Assert.Equal("infected", d.GetProperty("rejectedReason").GetString());
        Assert.Equal("infected", d.GetProperty("scan").GetProperty("status").GetString());
        Assert.False(File.Exists(AreaPath(StorageArea.Temp, uploadId)));
        Assert.False(File.Exists(AreaPath(StorageArea.Objects, uploadId)));
        Assert.True(File.Exists(AreaPath(StorageArea.Quarantine, uploadId)));
        var storage = await StorageAsync(owner);
        Assert.Equal(0, storage.GetProperty("usedBytes").GetInt64());
        Assert.Equal(0, storage.GetProperty("reservedBytes").GetInt64());

        // no route serves it: the viewer descriptor, the download button and both owner tickets refuse
        using var visitor = await Public().GetAsync($"/api/public/content/{code}");
        Assert.Equal(HttpStatusCode.Gone, visitor.StatusCode);
        using var button = await Public().PostAsync($"/api/public/content/{code}/download", null);
        Assert.Equal(HttpStatusCode.Gone, button.StatusCode);
        using var inline = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
        Assert.Equal(HttpStatusCode.Gone, inline.StatusCode);
        using var download = await client.PostAsync($"/api/resources/{resourceId}/download", null);
        Assert.Equal(HttpStatusCode.Gone, download.StatusCode);

        // and even a ticket signed by the server itself cannot be minted for it
        await using var scope = api.Services.CreateAsyncScope();
        var minted = await scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Resources.Contracts.IDownloadGrants>()
            .MintAsync(new(resourceId, Zaagl.Modules.Resources.Contracts.DeliveryPurpose.Inline, null), CancellationToken.None);
        Assert.False(minted.IsSuccess);
    }

    [Fact]
    public async Task A_hash_on_the_blocklist_is_rejected_and_quarantined()
    {
        var owner = await P.CreateUserAsync(starter: false, "blocked");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(3000, fill: 99);
        var (uploadId, completed) = await UploadAsync(client, data);
        var resourceId = ResourceIdOf(completed);
        await using (var scope = api.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IContentHashBlocklist>().AddAsync(SHA256.HashData(data), "test", default)).IsSuccess);

        Assert.Null(await RunScanAsync(resourceId));

        using var detail = await client.GetAsync($"/api/resources/{resourceId}");
        var d = await BodyAsync(detail);
        Assert.Equal("blocked-hash", d.GetProperty("rejectedReason").GetString());
        Assert.True(File.Exists(AreaPath(StorageArea.Quarantine, uploadId)));
        Assert.False(File.Exists(AreaPath(StorageArea.Objects, uploadId)));
    }

    [Fact]
    public async Task A_stopped_scanner_leaves_the_upload_processing_then_scan_unavailable_and_nothing_is_served()
    {
        var owner = await P.CreateUserAsync(starter: false, "noclam");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (uploadId, completed) = await UploadAsync(client, Png(4000));
        var resourceId = ResourceIdOf(completed);
        var code = CodeOf(completed);

        api.Clamd.Behavior = ClamdBehavior.CloseWithoutReply;
        try
        {
            // attempts 1..n: the scan errors, the task is retried, the upload stays processing and is not servable
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = await RunScanAsync(resourceId);
                Assert.NotNull(error);
                using var status = await client.GetAsync($"/api/resources/{resourceId}/status");
                Assert.Equal("processing", (await BodyAsync(status)).GetProperty("state").GetString());
                using var visitor = await Public().GetAsync($"/api/public/content/{code}");
                Assert.Equal("processing", (await BodyAsync(visitor)).GetProperty("state").GetString());
                Assert.False(File.Exists(AreaPath(StorageArea.Objects, uploadId)));
            }
            using var owned = await client.PostAsync($"/api/resources/{resourceId}/download", null);
            Assert.Equal(HttpStatusCode.Conflict, owned.StatusCode); // owner included: not ready, not served

            // the retries are exhausted: rejected with its own reason (never the malware text), bytes deleted, quota released
            await KillScanAsync(resourceId);
        }
        finally
        {
            api.Clamd.Behavior = ClamdBehavior.Normal;
        }
        using var detail = await client.GetAsync($"/api/resources/{resourceId}");
        var d = await BodyAsync(detail);
        Assert.Equal("rejected", d.GetProperty("state").GetString());
        Assert.Equal("scan-unavailable", d.GetProperty("rejectedReason").GetString());
        Assert.False(File.Exists(AreaPath(StorageArea.Temp, uploadId)));
        Assert.Equal(0, (await StorageAsync(owner)).GetProperty("reservedBytes").GetInt64());
    }

    [Fact]
    public async Task A_crash_after_the_rename_is_finished_by_the_next_attempt()
    {
        var owner = await P.CreateUserAsync(starter: false, "crash");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(6000);
        var (uploadId, completed) = await UploadAsync(client, data);
        var resourceId = ResourceIdOf(completed);

        // the previous attempt renamed temp to objects and died before updating the row
        await using (var scope = api.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IObjectStorage>().MoveAsync(StorageKeys.Create(StorageArea.Temp, uploadId), StorageArea.Objects, default);

        Assert.Null(await RunScanAsync(resourceId));

        using var detail = await client.GetAsync($"/api/resources/{resourceId}");
        Assert.Equal("ready", (await BodyAsync(detail)).GetProperty("state").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), (await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"))).GetProperty("file").GetProperty("sha256").GetString());
    }

    // ---- tus: resume, offsets, races ---------------------------------------------------------------

    [Fact]
    public async Task An_interrupted_upload_resumes_from_the_offset_head_reports()
    {
        var owner = await P.CreateUserAsync(starter: false, "resume");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(300_000);
        var id = await CreateSessionAsync(client, data.Length);

        // The connection dies after 120,000 bytes of a 300,000-byte chunk: the service keeps what reached the disk.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<UploadService>();
            var actor = new UploadActor(owner.Id, null, Zaagl.Modules.Resources.Contracts.CreatedVia.Web);
            await Assert.ThrowsAsync<IOException>(() =>
                service.AppendAsync(actor, id, 0, data.Length, new BreakingStream(data, 120_000), CancellationToken.None));
        }

        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}")))
            Assert.Equal("120000", head.Headers.GetValues("Upload-Offset").Single());

        // a wrong offset is refused with the real one in the message; the right one continues
        using (var wrong = await client.SendAsync(PatchRequest(id, 0, data[..1000])))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
            Assert.Equal("upload-offset-mismatch", (await BodyAsync(wrong)).GetProperty("code").GetString());
        }
        using (var resumed = await client.SendAsync(PatchRequest(id, 120_000, data[120_000..])))
        {
            Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
            Assert.Equal("300000", resumed.Headers.GetValues("Upload-Offset").Single());
        }

        using var completed = await client.SendAsync(CompleteRequest(id));
        Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode);
        var resourceId = ResourceIdOf(await BodyAsync(completed));
        Assert.Null(await RunScanAsync(resourceId));
        var detail = await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), detail.GetProperty("file").GetProperty("sha256").GetString());
    }

    private sealed class BreakingStream(byte[] data, int breakAfter) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= breakAfter) throw new IOException("connection reset");
            var n = Math.Min(Math.Min(buffer.Length, data.Length - _position), breakAfter - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            await Task.Yield();
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Parallel_patches_of_one_session_let_exactly_one_win()
    {
        var owner = await P.CreateUserAsync(starter: false, "race");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(2_000_000);
        var id = await CreateSessionAsync(client, data.Length);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var response = await client.SendAsync(PatchRequest(id, 0, data));
            return response.StatusCode;
        }));

        Assert.Equal(1, attempts.Count(s => s == HttpStatusCode.NoContent));
        Assert.All(attempts.Where(s => s != HttpStatusCode.NoContent), s => Assert.True(s is HttpStatusCode.Conflict));
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}"));
        Assert.Equal(data.Length.ToString(), head.Headers.GetValues("Upload-Offset").Single());
    }

    [Fact]
    public async Task A_chunk_past_the_declared_length_or_with_the_wrong_type_is_refused()
    {
        var owner = await P.CreateUserAsync(starter: false, "limits");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var id = await CreateSessionAsync(client, 1000);

        using (var tooMuch = await client.SendAsync(PatchRequest(id, 0, Png(1001))))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMuch.StatusCode);
            Assert.Equal("file-too-large", (await BodyAsync(tooMuch)).GetProperty("code").GetString());
        }
        using (var wrongType = await client.SendAsync(PatchRequest(id, 0, Png(1000), "application/octet-stream")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        using (var noOffset = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/uploads/{id}")
               {
                   Content = new ByteArrayContent(Png(10)) { Headers = { ContentType = new MediaTypeHeaderValue("application/offset+octet-stream") } },
               }))
            Assert.Equal(HttpStatusCode.BadRequest, noOffset.StatusCode);
        using (var oldTus = await client.SendAsync(CreateRequest(100, tus: "0.2.0")))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, oldTus.StatusCode);
            Assert.Equal("1.0.0", oldTus.Headers.GetValues("Tus-Version").Single());
        }
        // nothing of the refused chunks stayed
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}"));
        Assert.Equal("0", head.Headers.GetValues("Upload-Offset").Single());
    }

    [Fact]
    public async Task Options_announces_the_extensions_and_the_plan_limit()
    {
        var free = await P.CreateUserAsync(starter: false, "opt");
        using var client = api.ApiClient(bearer: free.AccessToken);
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/uploads"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("1.0.0", response.Headers.GetValues("Tus-Version").Single());
        Assert.Equal("creation,termination,expiration", response.Headers.GetValues("Tus-Extension").Single());
        Assert.Equal("26214400", response.Headers.GetValues("Tus-Max-Size").Single());
        Assert.Equal("1.0.0", response.Headers.GetValues("Tus-Resumable").Single());
    }

    // ---- before the first byte: size, quota, disk ----------------------------------------------------

    [Fact]
    public async Task A_file_over_the_plan_limit_is_refused_with_413_before_any_byte_and_reserves_nothing()
    {
        var free = await P.CreateUserAsync(starter: false, "big");
        using var client = api.ApiClient(bearer: free.AccessToken);
        using var response = await client.SendAsync(CreateRequest(26_214_400 + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file-too-large", (await BodyAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, (await StorageAsync(free)).GetProperty("reservedBytes").GetInt64());

        var starter = await P.CreateUserAsync(starter: true, "bigstarter");
        using var starterClient = api.ApiClient(bearer: starter.AccessToken);
        using var over = await starterClient.SendAsync(CreateRequest(104_857_600 + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        using var exactly = await starterClient.SendAsync(CreateRequest(104_857_600));
        Assert.Equal(HttpStatusCode.Created, exactly.StatusCode); // the limit itself is allowed
        Assert.Equal(104_857_600, (await StorageAsync(starter)).GetProperty("reservedBytes").GetInt64());
        using var abort = await starterClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, exactly.Headers.Location!));
        Assert.Equal(HttpStatusCode.NoContent, abort.StatusCode);
    }

    [Fact]
    public async Task The_quota_refuses_with_403_quota_exceeded_and_abort_gives_the_reservation_back()
    {
        var free = await P.CreateUserAsync(starter: false, "quota");
        using var client = api.ApiClient(bearer: free.AccessToken);
        var ids = new List<Uri>();
        // Free: 250 MB quota, 25 MB per file: ten reservations of 25 MB fit, the eleventh does not.
        for (var i = 0; i < 10; i++)
        {
            using var ok = await client.SendAsync(CreateRequest(26_214_400));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            ids.Add(ok.Headers.Location!);
        }
        using var refused = await client.SendAsync(CreateRequest(26_214_400));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("quota-exceeded", (await BodyAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(10, (await StorageAsync(free)).GetProperty("openUploads").GetInt32());

        using (var abort = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, ids[0])))
            Assert.Equal(HttpStatusCode.NoContent, abort.StatusCode);
        using (var again = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, ids[0])))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode); // the first call aborts, later calls are 404
        using (var gone = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, ids[0])))
            Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);

        var storage = await StorageAsync(free);
        Assert.Equal(9L * 26_214_400, storage.GetProperty("reservedBytes").GetInt64());
        using var fits = await client.SendAsync(CreateRequest(26_214_400));
        Assert.Equal(HttpStatusCode.Created, fits.StatusCode);
        foreach (var uri in ids.Skip(1).Append(fits.Headers.Location!))
            (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, uri))).Dispose();
        Assert.Equal(0, (await StorageAsync(free)).GetProperty("reservedBytes").GetInt64());
    }

    [Fact]
    public async Task A_full_disk_refuses_new_sessions_with_507_and_retry_after_and_takes_no_reservation()
    {
        var owner = await P.CreateUserAsync(starter: false, "disk");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        api.Disk.Full = true;
        try
        {
            using var response = await client.SendAsync(CreateRequest(10 * 1024 * 1024));
            Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
            Assert.Equal("insufficient-storage", (await BodyAsync(response)).GetProperty("code").GetString());
            Assert.Equal("300", response.Headers.GetValues("Retry-After").Single());
            Assert.Equal(0, (await StorageAsync(owner)).GetProperty("reservedBytes").GetInt64());
        }
        finally
        {
            api.Disk.Full = false;
        }
        using var ok = await client.SendAsync(CreateRequest(10 * 1024 * 1024));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, ok.Headers.Location!))).Dispose();
    }

    [Fact]
    public async Task Disk_admission_counts_the_bytes_still_expected_from_open_sessions_and_the_soft_close()
    {
        var owner = await P.CreateUserAsync(starter: true, "diskmath");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var probeBefore = (api.Disk.Total, api.Disk.Free);
        try
        {
            // 1 GiB capacity, 85% used: the soft close (80%) caps new sessions at 100 MB.
            api.Disk.Total = 1L << 30;
            api.Disk.Free = (long)((1L << 30) * 0.15);
            using var small = await client.SendAsync(CreateRequest(1_000_000));
            Assert.Equal(HttpStatusCode.Created, small.StatusCode);
            using var large = await client.SendAsync(CreateRequest(100_000_001));
            Assert.Equal(HttpStatusCode.InsufficientStorage, large.StatusCode);
            (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, small.Headers.Location!))).Dispose();
        }
        finally
        {
            (api.Disk.Total, api.Disk.Free) = probeBefore;
        }
    }

    // ---- creation validation and who may do what -----------------------------------------------------

    [Fact]
    public async Task Create_validation_kind_length_and_credentials()
    {
        var owner = await P.CreateUserAsync(starter: true, "valid");
        using var client = api.ApiClient(bearer: owner.AccessToken);

        async Task<(HttpStatusCode Status, string Code)> Send(HttpRequestMessage request)
        {
            using var response = await client.SendAsync(request);
            var body = await BodyAsync(response);
            return (response.StatusCode, body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var c) ? c.GetString()! : "");
        }

        Assert.Equal((HttpStatusCode.BadRequest, "validation"), await Send(CreateRequest(100, kind: "file")));          // S1: file is S2
        Assert.Equal((HttpStatusCode.BadRequest, "validation"), await Send(CreateRequest(100, kind: null)));
        Assert.Equal((HttpStatusCode.BadRequest, "validation"), await Send(CreateRequest(100, kind: "video")));
        Assert.Equal((HttpStatusCode.BadRequest, "validation"), await Send(CreateRequest(0)));                          // zero length
        Assert.Equal((HttpStatusCode.BadRequest, "upload-length-required"), await Send(CreateRequest(null)));
        Assert.Equal((HttpStatusCode.BadRequest, "upload-length-required"), await Send(CreateRequest(100, defer: true)));

        // no credential: 503 guest_creation_paused in S1; a failing credential is a 401, never a guest upload
        using var anonymous = api.ApiClient();
        using var guest = await anonymous.SendAsync(CreateRequest(100));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, guest.StatusCode);
        Assert.Equal("guest_creation_paused", (await BodyAsync(guest)).GetProperty("code").GetString());
        using var bad = api.ApiClient(bearer: "not-a-token");
        using var failed = await bad.SendAsync(CreateRequest(100));
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);

        // API keys: files:write is required to create, and resources:read does not substitute for it
        var readKey = await P.CreateApiKeyAsync(owner, "resources:read");
        using var readClient = api.ApiClient(apiKey: readKey);
        using var forbidden = await readClient.SendAsync(CreateRequest(100));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("insufficient-scope", (await BodyAsync(forbidden)).GetProperty("code").GetString());
        var writeKey = await P.CreateApiKeyAsync(owner, "files:write", "resources:read");
        using var writeClient = api.ApiClient(apiKey: writeKey);
        using var created = await writeClient.SendAsync(CreateRequest(100));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        (await writeClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, created.Headers.Location!))).Dispose();
    }

    [Fact]
    public async Task A_session_belongs_to_its_creator_alone()
    {
        var owner = await P.CreateUserAsync(starter: false, "own");
        var other = await P.CreateUserAsync(starter: false, "other");
        using var ownerClient = api.ApiClient(bearer: owner.AccessToken);
        using var otherClient = api.ApiClient(bearer: other.AccessToken);
        using var anonymous = api.ApiClient();
        var data = Png(500);
        var id = await CreateSessionAsync(ownerClient, data.Length);

        foreach (var client in new[] { otherClient, anonymous })
        {
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}"));
            Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
            using var patch = await client.SendAsync(PatchRequest(id, 0, data));
            Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
            using var delete = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/uploads/{id}"));
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }
        await PatchAllAsync(ownerClient, id, data);
        using var stolen = await otherClient.SendAsync(CompleteRequest(id));
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        using var mine = await ownerClient.SendAsync(CompleteRequest(id));
        Assert.Equal(HttpStatusCode.Accepted, mine.StatusCode);
    }

    // ---- /complete ---------------------------------------------------------------------------------

    [Fact]
    public async Task Complete_rules_incomplete_type_burn_password_expiry_and_replay()
    {
        var free = await P.CreateUserAsync(starter: false, "complete");
        using var client = api.ApiClient(bearer: free.AccessToken);

        // incomplete: 409 upload-incomplete
        var partial = await CreateSessionAsync(client, 1000);
        using (var early = await client.SendAsync(CompleteRequest(partial)))
        {
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.Equal("upload-incomplete", (await BodyAsync(early)).GetProperty("code").GetString());
        }
        // missing Idempotency-Key: 400
        await PatchAllAsync(client, partial, Png(1000));
        using (var noKey = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/uploads/{partial}/complete") { Content = JsonContent.Create(new { }) }))
            Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        // S1: burn is 400 validation; password needs Starter (402); expiry beyond the plan is 400
        using (var burn = await client.SendAsync(CompleteRequest(partial, new { burnAfterDownload = true })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, burn.StatusCode);
            Assert.Equal("validation", (await BodyAsync(burn)).GetProperty("code").GetString());
        }
        using (var password = await client.SendAsync(CompleteRequest(partial, new { password = "secret-pass" })))
            Assert.Equal(HttpStatusCode.PaymentRequired, password.StatusCode);
        using (var beyond = await client.SendAsync(CompleteRequest(partial, new { expiresAt = DateTime.UtcNow.AddDays(30) })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, beyond.StatusCode);
            Assert.Equal("expiry-beyond-plan", (await BodyAsync(beyond)).GetProperty("code").GetString());
        }

        // success, then replay: same key and body returns the stored answer without the key; another key is 409
        var key = "replay-" + TestIps.UniqueSuffix();
        using var first = await client.SendAsync(CompleteRequest(partial, new { title = "T", @public = false }, key));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstBody = await BodyAsync(first);
        Assert.False(string.IsNullOrEmpty(firstBody.GetProperty("linkKey").GetString()));
        using var replay = await client.SendAsync(CompleteRequest(partial, new { title = "T", @public = false }, key));
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayBody = await BodyAsync(replay);
        Assert.Equal(CodeOf(firstBody), CodeOf(replayBody));
        Assert.Equal(ResourceIdOf(firstBody), ResourceIdOf(replayBody));
        Assert.Equal(JsonValueKind.Null, replayBody.GetProperty("linkKey").ValueKind); // ARC-002 Q1 (OPEN)
        using var otherKey = await client.SendAsync(CompleteRequest(partial, new { title = "T", @public = false }, "other-" + TestIps.UniqueSuffix()));
        Assert.Equal(HttpStatusCode.Conflict, otherKey.StatusCode);
        Assert.Equal("already-finalized", (await BodyAsync(otherKey)).GetProperty("code").GetString());
        using var otherBody = await client.SendAsync(CompleteRequest(partial, new { title = "Different" }, key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherBody.StatusCode);
        Assert.Equal("idempotency-mismatch", (await BodyAsync(otherBody)).GetProperty("code").GetString());

        // exactly one scan task and one quota reservation for it (the retries did not duplicate anything)
        var resourceId = ResourceIdOf(firstBody);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM files.tasks WHERE dedupe_key = @k", ("k", $"scan:{resourceId}")));
        Assert.Equal(1000, (await StorageAsync(free)).GetProperty("reservedBytes").GetInt64());
    }

    [Theory]
    [InlineData("html", "<html><body><script>alert(1)</script></body></html>")]
    [InlineData("svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>")]
    [InlineData("exe", "MZ\u0090\0\u0003\0\0\0\u0004\0\0\0")]
    [InlineData("text", "just some text")]
    public async Task Non_image_bytes_declared_as_media_are_refused_at_complete_by_what_they_are_not_by_their_name(string name, string content)
    {
        var owner = await P.CreateUserAsync(starter: false, "type-" + name);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Encoding.Latin1.GetBytes(content);
        var id = await CreateSessionAsync(client, data.Length);
        await PatchAllAsync(client, id, data);

        // named .png and declared image/png: the bytes decide
        using var response = await client.SendAsync(CompleteRequest(id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("content-rejected", body.GetProperty("code").GetString());
        Assert.Equal("type-not-allowed", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_display_name_is_sanitised_and_the_declared_type_never_decides()
    {
        var owner = await P.CreateUserAsync(starter: false, "names");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(2000);
        using var created = await client.SendAsync(CreateRequest(data.Length, filename: @"..\..\evil.exe"));
        var id = IdFromLocation(created);
        await PatchAllAsync(client, id, data);
        using var completed = await client.SendAsync(CompleteRequest(id));
        var resourceId = ResourceIdOf(await BodyAsync(completed));
        Assert.Null(await RunScanAsync(resourceId));

        var detail = await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"));
        Assert.Equal("evil.exe.png", detail.GetProperty("filename").GetString()); // last segment, detected extension appended
        Assert.Equal("image/png", detail.GetProperty("mime").GetString());
    }

    // ---- tickets and delivery -----------------------------------------------------------------------

    private async Task<(TestUser Owner, HttpClient Client, byte[] Data, Guid ResourceId, string Code, Guid UploadId)> ReadyImageAsync(
        string tag, int size = 20_000, bool keyed = false)
    {
        var owner = await P.CreateUserAsync(starter: false, tag);
        var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(size, fill: (byte)(size % 200));
        var (uploadId, completed) = await UploadAsync(client, data, new { @public = !keyed });
        var resourceId = ResourceIdOf(completed);
        Assert.Null(await RunScanAsync(resourceId));
        return (owner, client, data, resourceId, CodeOf(completed), uploadId);
    }

    [Fact]
    public async Task Download_and_owner_tickets_serve_the_original_as_an_attachment_and_count_correctly()
    {
        var (owner, client, data, resourceId, code, _) = await ReadyImageAsync("tickets");
        using var _client = client;
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));

        // owner ticket: attachment, never counts as a download
        using var ownerTicket = await client.PostAsync($"/api/resources/{resourceId}/download", null);
        Assert.Equal(HttpStatusCode.OK, ownerTicket.StatusCode);
        using var ownerFile = await FetchAsync((await BodyAsync(ownerTicket)).GetProperty("url").GetString()!);
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(await ownerFile.Content.ReadAsByteArrayAsync())));
        Assert.StartsWith("attachment;", ownerFile.Content.Headers.ContentDisposition!.ToString());
        Assert.False(ownerFile.Headers.Contains("X-Zaagl-Acao"));
        var detail = await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"));
        Assert.Equal(0, detail.GetProperty("downloadCount").GetInt64());

        // the visitor's download button: a POST mints, the first GET counts, HEAD never counts
        using var button = await Public().PostAsync($"/api/public/content/{code}/download", null);
        Assert.Equal(HttpStatusCode.OK, button.StatusCode);
        var buttonBody = await BodyAsync(button);
        var url = buttonBody.GetProperty("url").GetString()!;
        using (var head = await FetchAsync(url, method: HttpMethod.Head))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(data.Length, head.Content.Headers.ContentLength);
        }
        Assert.Equal(0, (await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"))).GetProperty("downloadCount").GetInt64());
        using (var file = await FetchAsync(url))
        {
            Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(await file.Content.ReadAsByteArrayAsync())));
            var disposition = file.Content.Headers.ContentDisposition!.ToString();
            Assert.StartsWith("attachment;", disposition);
            Assert.Contains("pic.png", disposition);
        }
        Assert.Equal(1, (await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"))).GetProperty("downloadCount").GetInt64());

        // the inline ticket of the owner and the viewer's inline ticket do not count either
        using var inline = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
        using var inlineFile = await FetchAsync((await BodyAsync(inline)).GetProperty("url").GetString()!);
        Assert.StartsWith("inline;", inlineFile.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(1, (await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"))).GetProperty("downloadCount").GetInt64());
    }

    [Fact]
    public async Task A_tampered_expired_or_unknown_ticket_is_403_ticket_invalid_with_the_inert_headers()
    {
        var (_, client, _, resourceId, _, _) = await ReadyImageAsync("tamper");
        using var _client = client;
        using var minted = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
        var url = (await BodyAsync(minted)).GetProperty("url").GetString()!;
        var ticket = TicketOf(url);

        // flip a character of the payload and of the signature
        var dot = ticket.IndexOf('.');
        string Flip(string s, int i) => s[..i] + (s[i] == 'A' ? 'B' : 'A') + s[(i + 1)..];
        foreach (var bad in new[] { Flip(ticket, 10), Flip(ticket, dot + 5), ticket[..dot], ticket + "x", "garbage", new string('a', 300) + "." + new string('b', 43) })
        {
            using var response = await FetchAsync("/d/" + bad);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("ticket-invalid", (await BodyAsync(response)).GetProperty("code").GetString());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task A_ticket_stops_working_when_the_link_is_deleted_and_never_crosses_resources()
    {
        var (_, client, _, resourceId, code, _) = await ReadyImageAsync("revoke");
        using var _client = client;
        var (_, client2, _, resourceId2, _, _) = await ReadyImageAsync("revoke2");
        using var _client2 = client2;

        using var minted = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
        var url = (await BodyAsync(minted)).GetProperty("url").GetString()!;
        using (var served = await FetchAsync(url)) Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        // another account cannot mint for this resource (404) nor read it
        using (var crossMint = await client2.PostAsync($"/api/resources/{resourceId}/inline", null)) Assert.Equal(HttpStatusCode.NotFound, crossMint.StatusCode);
        using (var crossRead = await client2.GetAsync($"/api/resources/{resourceId}")) Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);
        using (var crossDelete = await client2.DeleteAsync($"/api/resources/{resourceId}")) Assert.Equal(HttpStatusCode.NotFound, crossDelete.StatusCode);
        using (var own = await client2.GetAsync($"/api/resources/{resourceId2}")) Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // delete: the link answers 410 at once, the standing ticket is refused, the owner library still lists it as deleted
        using (var deleted = await client.DeleteAsync($"/api/resources/{resourceId}")) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var after = await FetchAsync(url)) Assert.Equal(HttpStatusCode.Gone, after.StatusCode);
        using (var visitor = await Public().GetAsync($"/api/public/content/{code}")) Assert.Equal(HttpStatusCode.Gone, visitor.StatusCode);
        var detail = await BodyAsync(await client.GetAsync($"/api/resources/{resourceId}"));
        Assert.Equal("deleted", detail.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Range_requests_work_and_a_file_over_the_response_cap_needs_one_acceptable_range()
    {
        var (_, client, data, resourceId, _, _) = await ReadyImageAsync("range", size: 300_000); // cap is 200,000
        using var _client = client;
        using var minted = await client.PostAsync($"/api/resources/{resourceId}/download", null);
        var url = (await BodyAsync(minted)).GetProperty("url").GetString()!;

        // no range, an open range that is too long, several ranges: 416 range-required with the size
        foreach (var range in new string?[] { null, "bytes=0-", "bytes=0-249999", "bytes=0-9,20-29", "bytes=-250000", "items=0-5" })
        {
            using var refused = await FetchAsync(url, range);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, refused.StatusCode);
            Assert.Equal("range-required", (await BodyAsync(refused)).GetProperty("code").GetString());
            Assert.Equal("bytes", refused.Headers.GetValues("Accept-Ranges").Single());
            Assert.Equal($"bytes */{data.Length}", refused.Content.Headers.ContentRange?.ToString() ?? string.Join("", refused.Headers.GetValues("Content-Range")));
        }
        // acceptable ranges: 206 with exactly those bytes; two parts reassemble the file
        using var first = await FetchAsync(url, "bytes=0-149999");
        using var second = await FetchAsync(url, "bytes=150000-");
        Assert.Equal(HttpStatusCode.PartialContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, second.StatusCode);
        var joined = (await first.Content.ReadAsByteArrayAsync()).Concat(await second.Content.ReadAsByteArrayAsync()).ToArray();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), Convert.ToHexStringLower(SHA256.HashData(joined)));
        using var suffix = await FetchAsync(url, "bytes=-100");
        Assert.Equal(data[^100..], await suffix.Content.ReadAsByteArrayAsync());
        // a refused request consumed nothing: HEAD of a capped object is allowed and reports the size
        using var head = await FetchAsync(url, method: HttpMethod.Head);
        Assert.Equal(data.Length, head.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, 100, true)]
    [InlineData("bytes=0-100", 1000, 100, false)]
    [InlineData("bytes=900-", 1000, 100, true)]
    [InlineData("bytes=800-", 1000, 100, false)]
    [InlineData("bytes=-100", 1000, 100, true)]
    [InlineData("bytes=-101", 1000, 100, false)]
    [InlineData("bytes=0-9,20-29", 1000, 100, false)]
    [InlineData("bytes=5000-6000", 1000, 100, false)]
    [InlineData("", 1000, 100, false)]
    [InlineData("pages=1-2", 1000, 100, false)]
    public void The_range_rule_for_capped_objects(string header, long size, long cap, bool expected) =>
        Assert.Equal(expected, Zaagl.Api.Infrastructure.ObjectDelivery.RangeAcceptable(header, size, cap));

    [Fact]
    public async Task For_nginx_the_hop_answers_204_with_a_validated_x_accel_redirect_and_never_a_temp_or_quarantine_path()
    {
        var (_, client, _, resourceId, _, uploadId) = await ReadyImageAsync("accel");
        using var _client = client;
        var options = api.Services.GetRequiredService<IOptions<ContentOptions>>().Value;
        options.DirectObjectServing = false;
        try
        {
            using var minted = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
            using var response = await FetchAsync((await BodyAsync(minted)).GetProperty("url").GetString()!);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var path = response.Headers.GetValues("X-Accel-Redirect").Single();
            Assert.Equal("/_protected/" + StorageKeys.Create(StorageArea.Objects, uploadId), path);
            Assert.Matches(@"^/_protected/objects/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f-]{36}$", path);
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        }
        finally
        {
            options.DirectObjectServing = true;
        }
    }

    [Fact]
    public async Task A_ready_resource_whose_object_is_not_clean_or_not_in_objects_is_never_served()
    {
        var (_, client, _, resourceId, _, uploadId) = await ReadyImageAsync("failclosed");
        using var _client = client;
        using var minted = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
        var url = (await BodyAsync(minted)).GetProperty("url").GetString()!;
        using (var ok = await FetchAsync(url)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // the row says the scan is not clean: 410, whatever the resource says
        await ScalarAsync("UPDATE files.storage_objects SET scan_status = 3 WHERE id = @i; SELECT 1", ("i", uploadId));
        using (var notClean = await FetchAsync(url)) Assert.Equal(HttpStatusCode.Gone, notClean.StatusCode);
        await ScalarAsync("UPDATE files.storage_objects SET scan_status = 1 WHERE id = @i; SELECT 1", ("i", uploadId));

        // the file is gone from objects: 410 and no 404 from storage
        var path = AreaPath(StorageArea.Objects, uploadId);
        var moved = path + ".gone";
        File.Move(path, moved);
        try
        {
            using var missing = await FetchAsync(url);
            Assert.Equal(HttpStatusCode.Gone, missing.StatusCode);
        }
        finally
        {
            File.Move(moved, path);
        }
        using (var back = await FetchAsync(url)) Assert.Equal(HttpStatusCode.OK, back.StatusCode);
    }

    // ---- owner library --------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_library_lists_pages_filters_and_patches()
    {
        var owner = await P.CreateUserAsync(starter: true, "library");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (_, completed) = await UploadAsync(client, Png(1000 + i, fill: (byte)(30 + i)), new { @public = true, title = $"item {i}" });
            ids.Add(ResourceIdOf(completed));
        }
        Assert.Null(await RunScanAsync(ids[0]));
        Assert.Null(await RunScanAsync(ids[1]));

        // page 1 (limit 2, newest first), page 2 by cursor
        var page1 = await BodyAsync(await client.GetAsync("/api/resources?limit=2"));
        var items1 = page1.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items1.Count);
        Assert.Equal(ids[2], items1[0].GetProperty("id").GetGuid());
        Assert.Equal(ids[1], items1[1].GetProperty("id").GetGuid());
        var cursor = page1.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var page2 = await BodyAsync(await client.GetAsync($"/api/resources?limit=2&cursor={Uri.EscapeDataString(cursor!)}"));
        var items2 = page2.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items2);
        Assert.Equal(ids[0], items2[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);

        // filters
        var processing = await BodyAsync(await client.GetAsync("/api/resources?state=processing"));
        Assert.Equal([ids[2]], processing.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        var ready = await BodyAsync(await client.GetAsync("/api/resources?kind=media&state=ready"));
        Assert.Equal(2, ready.GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await BodyAsync(await client.GetAsync("/api/resources?kind=snippet"))).GetProperty("items").GetArrayLength());
        using (var badKind = await client.GetAsync("/api/resources?kind=bogus")) Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        using (var badCursor = await client.GetAsync("/api/resources?cursor=%25%25")) Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);

        // the list item shape: no link key, no thumbnail in S1, filename and mime for media
        var first = items1[1];
        Assert.False(first.TryGetProperty("linkKey", out var lk) && lk.ValueKind != JsonValueKind.Null);
        Assert.Equal("image/png", first.GetProperty("mime").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("thumbnailUrl").ValueKind);
        Assert.StartsWith("http://zaa.test/", first.GetProperty("shortUrl").GetString());
        Assert.True(first.GetProperty("isPublic").GetBoolean());

        // patch: title, renewal (forward only, within the plan), public off issues a key once, burn is S2
        async Task<(HttpStatusCode, JsonElement)> Patch(object body)
        {
            using var response = await client.PatchAsJsonAsync($"/api/resources/{ids[1]}", body, Json);
            return (response.StatusCode, await BodyAsync(response));
        }
        var (okStatus, titled) = await Patch(new { title = "renamed" });
        Assert.Equal(HttpStatusCode.OK, okStatus);
        Assert.Equal("renamed", titled.GetProperty("title").GetString());
        var (_, cleared) = await Patch(new { title = (string?)null });
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("title").ValueKind);

        var (beyondStatus, beyond) = await Patch(new { expiresAt = DateTime.UtcNow.AddDays(400) });
        Assert.Equal(HttpStatusCode.BadRequest, beyondStatus);
        Assert.Equal("expiry-beyond-plan", beyond.GetProperty("code").GetString());
        var (backwardsStatus, _) = await Patch(new { expiresAt = DateTime.UtcNow.AddHours(1) });
        Assert.Equal(HttpStatusCode.BadRequest, backwardsStatus);
        var current = DateTime.Parse(titled.GetProperty("expiresAt").GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var (renewStatus, renewed) = await Patch(new { expiresAt = current.AddDays(-1).AddHours(23).AddMinutes(59) });
        Assert.Equal(HttpStatusCode.BadRequest, renewStatus); // not forward of the current expiry

        var (privateStatus, privateBody) = await Patch(new { @public = false });
        Assert.Equal(HttpStatusCode.OK, privateStatus);
        Assert.False(privateBody.GetProperty("isPublic").GetBoolean());
        Assert.False(string.IsNullOrEmpty(privateBody.GetProperty("linkKey").GetString())); // the new key, once
        var (publicStatus, publicBody) = await Patch(new { @public = true });
        Assert.Equal(HttpStatusCode.OK, publicStatus);
        Assert.True(publicBody.GetProperty("isPublic").GetBoolean());
        Assert.Equal(JsonValueKind.Null, publicBody.GetProperty("linkKey").ValueKind);

        var (burnStatus, burn) = await Patch(new { burnAfterDownload = true });
        Assert.Equal(HttpStatusCode.BadRequest, burnStatus);
        Assert.Equal("validation", burn.GetProperty("code").GetString());
        var (pwStatus, _) = await Patch(new { password = "abcd" });
        Assert.Equal(HttpStatusCode.OK, pwStatus); // Starter may set a password on content
        var (shortPw, _) = await Patch(new { password = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPw);

        // a key without files:write cannot patch or delete; with it, it can
        var readKey = await P.CreateApiKeyAsync(owner, "resources:read");
        using var readClient = api.ApiClient(apiKey: readKey);
        using (var read = await readClient.GetAsync($"/api/resources/{ids[1]}")) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var noWrite = await readClient.PatchAsJsonAsync($"/api/resources/{ids[1]}", new { title = "x" }, Json)) Assert.Equal(HttpStatusCode.Forbidden, noWrite.StatusCode);
        using (var noDelete = await readClient.DeleteAsync($"/api/resources/{ids[1]}")) Assert.Equal(HttpStatusCode.Forbidden, noDelete.StatusCode);
    }

    [Fact]
    public async Task A_keyed_link_needs_its_key_and_a_wrong_key_looks_like_an_unknown_code()
    {
        var (_, client, _, _, code, _) = await ReadyImageAsync("keyed", keyed: true);
        using var _client = client;

        using var noKey = await Public().GetAsync($"/api/public/content/{code}");
        Assert.Equal(HttpStatusCode.NotFound, noKey.StatusCode);
        using var wrong = await Public("AAAAAAAAAAAAAAAAAAAAAA").GetAsync($"/api/public/content/{code}");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        using var button = await Public().PostAsync($"/api/public/content/{code}/download", null);
        Assert.Equal(HttpStatusCode.NotFound, button.StatusCode);
    }

    // ---- sessions expire ------------------------------------------------------------------------------

    [Fact]
    public async Task An_idle_session_expires_its_bytes_are_deleted_the_reservation_is_released_and_it_answers_410()
    {
        var owner = await P.CreateUserAsync(starter: false, "expiry");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Png(3000);
        var id = await CreateSessionAsync(client, data.Length);
        using (var part = await client.SendAsync(PatchRequest(id, 0, data[..1000]))) Assert.Equal(HttpStatusCode.NoContent, part.StatusCode);
        Assert.True(File.Exists(AreaPath(StorageArea.Temp, id)));
        Assert.Equal(3000, (await StorageAsync(owner)).GetProperty("reservedBytes").GetInt64());

        // 24 hours of silence
        await ScalarAsync("UPDATE files.upload_sessions SET expires_at = now() - interval '1 minute' WHERE id = @i; SELECT 1", ("i", id));
        using (var late = await client.SendAsync(PatchRequest(id, 1000, data[1000..]))) // a PATCH racing the job still gets 410
        {
            Assert.Equal(HttpStatusCode.Gone, late.StatusCode);
            Assert.Equal("upload-expired", (await BodyAsync(late)).GetProperty("code").GetString());
        }
        await using (var scope = api.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<UploadSessionExpiry>().ExpireAsync(CancellationToken.None) >= 1);

        Assert.False(File.Exists(AreaPath(StorageArea.Temp, id)));
        Assert.Equal(0, (await StorageAsync(owner)).GetProperty("reservedBytes").GetInt64());
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{id}"));
        Assert.Equal(HttpStatusCode.Gone, head.StatusCode);
        using var complete = await client.SendAsync(CompleteRequest(id));
        Assert.Equal(HttpStatusCode.Gone, complete.StatusCode);
    }
}

/// <summary>Ticket lifetimes move the shared test clock, which would invalidate the bearer tokens other tests mint, so this lives in its own fixture.</summary>
[Trait("Area", "BE-017")]
public sealed class FilesTicketExpiryTests(FilesApiFactory api) : IClassFixture<FilesApiFactory>
{
    [Fact]
    public async Task Inline_tickets_live_15_minutes_and_download_tickets_120_seconds_unused()
    {
        var owner = await api.Principals.CreateUserAsync(starter: false, "ttl");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = new byte[3000];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);

        using var created = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/uploads")
        {
            Headers =
            {
                { "Upload-Length", data.Length.ToString() },
                { "Upload-Metadata", $"kind {Convert.ToBase64String("media"u8.ToArray())},filename {Convert.ToBase64String("t.png"u8.ToArray())}" },
            },
        });
        var id = Guid.Parse(created.Headers.Location!.OriginalString["/api/uploads/".Length..]);
        using var patch = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/uploads/{id}")
        {
            Headers = { { "Upload-Offset", "0" } },
            Content = new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue("application/offset+octet-stream") } },
        });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        using var completed = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/uploads/{id}/complete")
        {
            Headers = { { "Idempotency-Key", "ttl-" + TestIps.UniqueSuffix() } },
            Content = JsonContent.Create(new { @public = true }, options: TestPrincipals.Json),
        });
        var body = JsonDocument.Parse(await completed.Content.ReadAsStringAsync()).RootElement;
        var resourceId = body.GetProperty("resourceId").GetGuid();

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
            var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.DedupeKey == $"scan:{resourceId}");
            await scope.ServiceProvider.GetRequiredService<ScanTaskHandler>()
                .HandleAsync(new ClaimedTask(row.Id, row.Kind, row.DedupeKey, row.Payload, 1, row.MaxAttempts), CancellationToken.None);
        }

        using var inline = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
        var inlineUrl = JsonDocument.Parse(await inline.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;
        using var button = await api.ApiClient().PostAsync($"/api/public/content/{body.GetProperty("code").GetString()}/download", null);
        var downloadUrl = JsonDocument.Parse(await button.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;
        using var inlineAgain = await client.PostAsync($"/api/resources/{resourceId}/inline", null);
        var stillUnused = JsonDocument.Parse(await inlineAgain.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;

        async Task<HttpStatusCode> Fetch(string url)
        {
            using var host = api.HostClient(FilesApiFactory.UserContentHost);
            using var response = await host.GetAsync("/d/" + url[(url.IndexOf("/d/", StringComparison.Ordinal) + 3)..]);
            return response.StatusCode;
        }

        // no bearer is used from here on: the clock moves
        api.Clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(HttpStatusCode.Forbidden, await Fetch(downloadUrl));    // unused download ticket: 120 s
        Assert.Equal(HttpStatusCode.OK, await Fetch(inlineUrl));             // inline ticket: 15 minutes
        api.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.Forbidden, await Fetch(inlineUrl));
        Assert.Equal(HttpStatusCode.Forbidden, await Fetch(stillUnused));
    }
}