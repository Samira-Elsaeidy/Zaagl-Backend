using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SkiaSharp;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Api.Tests;

/// <summary>FilesApiFactory plus the media-preview task handler hosted in-process (the worker runs it in production).</summary>
public sealed class MediaS3Factory : FilesApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<MediaConcurrencyGate>();
            services.AddScoped<ITaskHandler<FilesDbContext>, MediaPreviewTaskHandler>();
            services.AddScoped<MediaPreviewTaskHandler>();
            services.AddScoped<DeleteBytesTaskHandler>();
        });
    }
}

/// <summary>
/// BE-015 (E1.2 slice S3, ARC-011): probe and thumbnail after a clean scan, `media` and `thumbnailUrl` in the owner list, detail and the
/// public descriptor, the derivative delivery rules, the bomb corpus end to end, and original immutability (I1-I6), over HTTP on the real
/// Program and database with a fake clamd.
/// </summary>
[Trait("Area", "BE-015")]
public sealed class MediaS3Tests(MediaS3Factory api) : IClassFixture<MediaS3Factory>
{
    private static readonly JsonSerializerOptions Json = TestPrincipals.Json;
    private TestPrincipals P => api.Principals;

    // ---- fixtures -------------------------------------------------------------------------------------------

    private static byte[] Image(int width, int height, SKEncodedImageFormat format, bool alpha = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(alpha ? new SKColor(0, 128, 255, 100) : SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, format == SKEncodedImageFormat.Webp ? 80 : 100);
        return data.ToArray();
    }

    /// <summary>A valid PNG that inflates to width x height grey zeros (a real decompression bomb in about 100 KB).</summary>
    private static byte[] BombPng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        Chunk(ms, "IHDR", ihdr);
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++) z.Write(row);
        }
        Chunk(ms, "IDAT", deflated.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        s.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        Span<byte> check = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(check, ~crc);
        s.Write(check);
    }

    private static string B64(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v));
    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static string TicketOf(string url) => url[(url.IndexOf("/d/", StringComparison.Ordinal) + 3)..];

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private string AreaPath(StorageArea area, Guid id) =>
        Path.Combine(api.StorageRoot, StorageKeys.Create(area, id).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>tus create, one PATCH, /complete (public link). Returns the upload (object) id, resource id and code.</summary>
    private async Task<(Guid UploadId, Guid ResourceId, string Code)> UploadAsync(HttpClient client, byte[] data, string filename = "pic.png")
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/uploads");
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", data.Length.ToString());
        create.Headers.Add("Upload-Metadata", $"kind {B64("media")},filename {B64(filename)}");
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = Guid.Parse(created.Headers.Location!.OriginalString["/api/uploads/".Length..]);

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/uploads/{id}") { Content = new ByteArrayContent(data) };
        patch.Headers.Add("Tus-Resumable", "1.0.0");
        patch.Headers.Add("Upload-Offset", "0");
        patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        using var patched = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);

        var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/uploads/{id}/complete")
        {
            Content = JsonContent.Create(new { @public = true }, options: Json),
        };
        complete.Headers.Add("Idempotency-Key", "c-" + TestIps.UniqueSuffix());
        using var completed = await client.SendAsync(complete);
        Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode);
        var body = await BodyAsync(completed);
        return (id, body.GetProperty("resourceId").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private async Task RunTaskAsync(string dedupeKey)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.DedupeKey == dedupeKey && t.Status < QueuedTaskStatus.Done);
        var claimed = new ClaimedTask(row.Id, row.Kind, row.DedupeKey, row.Payload, 1, row.MaxAttempts);
        if (row.Kind == ScanTaskHandler.TaskKind) await scope.ServiceProvider.GetRequiredService<ScanTaskHandler>().HandleAsync(claimed, CancellationToken.None);
        else if (row.Kind == MediaPreviewTaskHandler.TaskKind) await scope.ServiceProvider.GetRequiredService<MediaPreviewTaskHandler>().HandleAsync(claimed, CancellationToken.None);
        else if (row.Kind == DeleteBytesTaskHandler.TaskKind) await scope.ServiceProvider.GetRequiredService<DeleteBytesTaskHandler>().HandleAsync(claimed, CancellationToken.None);
        else throw new InvalidOperationException(row.Kind);
    }

    private Task ScanAsync(Guid resourceId) => RunTaskAsync($"scan:{resourceId}");
    private Task MediaAsync(Guid resourceId) => RunTaskAsync($"media-preview:{resourceId}");

    /// <summary>upload, scan, media task.</summary>
    private async Task<(Guid UploadId, Guid ResourceId, string Code)> ReadyAsync(HttpClient client, byte[] data)
    {
        var up = await UploadAsync(client, data);
        await ScanAsync(up.ResourceId);
        await MediaAsync(up.ResourceId);
        return up;
    }

    private async Task<HttpResponseMessage> FetchAsync(string ticketUrl, HttpMethod? method = null)
    {
        using var client = api.HostClient(FilesApiFactory.UserContentHost);
        return await client.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Get, "/d/" + TicketOf(ticketUrl)), HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task<int> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestDb.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<JsonElement> ListItemAsync(HttpClient client, Guid resourceId)
    {
        using var list = await client.GetAsync("/api/resources?kind=media&limit=100");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await BodyAsync(list);
        return body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == resourceId);
    }

    // ---- the happy path -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_png_gets_metadata_and_a_thumbnail_and_the_original_is_never_touched()
    {
        var owner = await P.CreateUserAsync(starter: false, "thumb");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Image(1280, 720, SKEncodedImageFormat.Png);
        var up = await UploadAsync(client, data);
        await ScanAsync(up.ResourceId);

        // Ready, but the media task did not run yet: no metadata, no thumbnail, the library still lists it.
        var before = await ListItemAsync(client, up.ResourceId);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("thumbnailUrl").ValueKind);
        var original = AreaPath(StorageArea.Objects, up.UploadId);
        var hashBefore = Sha(File.ReadAllBytes(original));
        var mtimeBefore = File.GetLastWriteTimeUtc(original);
        Assert.Equal(Sha(data), hashBefore);

        await MediaAsync(up.ResourceId);

        // I1: the bytes and the timestamp of the original are exactly what they were; the stored hash did not change either.
        Assert.Equal(hashBefore, Sha(File.ReadAllBytes(original)));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(original));

        // owner list: thumbnailUrl and media
        var item = await ListItemAsync(client, up.ResourceId);
        var thumbUrl = item.GetProperty("thumbnailUrl").GetString();
        Assert.False(string.IsNullOrEmpty(thumbUrl));
        Assert.Contains("/d/", thumbUrl);
        Assert.Equal((1280, 720), (item.GetProperty("media").GetProperty("width").GetInt32(), item.GetProperty("media").GetProperty("height").GetInt32())); // dimensions for the library

        // owner detail: the media block (displayed size, no alpha, one ready thumb-320 derivative with a ticket)
        using var detail = await client.GetAsync($"/api/resources/{up.ResourceId}");
        var d = await BodyAsync(detail);
        var media = d.GetProperty("media");
        Assert.Equal("image", media.GetProperty("mediaType").GetString());
        Assert.Equal(1280, media.GetProperty("width").GetInt32());
        Assert.Equal(720, media.GetProperty("height").GetInt32());
        Assert.False(media.GetProperty("hasAlpha").GetBoolean());
        Assert.Equal(1, media.GetProperty("orientation").GetInt32());
        Assert.Equal(JsonValueKind.Null, media.GetProperty("hasLocationData").ValueKind); // PD-13 OPEN: no stripping and no promise
        var derivative = media.GetProperty("derivatives").EnumerateArray().Single();
        Assert.Equal("thumb-320", derivative.GetProperty("kind").GetString());
        Assert.Equal("ready", derivative.GetProperty("status").GetString());
        Assert.Equal(320, derivative.GetProperty("width").GetInt32());
        Assert.Equal(180, derivative.GetProperty("height").GetInt32());
        Assert.Contains("/d/", derivative.GetProperty("url").GetString());
        Assert.False(d.TryGetProperty("stripLocation", out _)); // no wire field for EXIF stripping

        // the thumbnail is served inline, as WebP, with the derivative's own bytes and the sandbox headers
        using var served = await FetchAsync(thumbUrl!);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline;", served.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("https://app.zaa.test", served.Headers.GetValues("X-Zaagl-Acao").Single());
        var thumbBytes = await served.Content.ReadAsByteArrayAsync();
        using (var codec = SKCodec.Create(new MemoryStream(thumbBytes)))
            Assert.Equal((320, 180, SKEncodedImageFormat.Webp), (codec.Info.Width, codec.Info.Height, codec.EncodedFormat));
        Assert.NotEqual(Sha(data), Sha(thumbBytes));

        // the derivative lives in thumbnails/ with its own id, not under the original's id
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var stored = (await scope.ServiceProvider.GetRequiredService<IMediaMetadata>().GetAsync([up.ResourceId], default))[up.ResourceId];
            var derivativeId = stored.Derivatives.Single().Id;
            Assert.NotEqual(up.UploadId, derivativeId);
            Assert.True(File.Exists(AreaPath(StorageArea.Thumbnails, derivativeId)));
        }

        // the original is still served byte for byte through its own Inline ticket
        using var inline = await client.PostAsync($"/api/resources/{up.ResourceId}/inline", null);
        var inlineUrl = (await BodyAsync(inline)).GetProperty("url").GetString()!;
        using var original200 = await FetchAsync(inlineUrl);
        Assert.Equal(Sha(data), Sha(await original200.Content.ReadAsByteArrayAsync()));
        Assert.Equal("image/png", original200.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task The_public_descriptor_carries_the_probe_result_and_the_thumbnail_ticket()
    {
        var owner = await P.CreateUserAsync(starter: false, "pubdesc");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(800, 400, SKEncodedImageFormat.Png, alpha: true));

        using var visitor = api.ApiClient();
        using var response = await visitor.GetAsync($"/api/public/content/{up.Code}");
        var d = await BodyAsync(response);
        Assert.Equal("ready", d.GetProperty("state").GetString());
        var media = d.GetProperty("media");
        Assert.Equal((800, 400, true), (media.GetProperty("width").GetInt32(), media.GetProperty("height").GetInt32(), media.GetProperty("hasAlpha").GetBoolean()));
        Assert.False(media.TryGetProperty("hasLocationData", out var gps) && gps.ValueKind != JsonValueKind.Null); // never in a public response
        var previewUrl = d.GetProperty("previewUrl").GetString();
        Assert.False(string.IsNullOrEmpty(previewUrl));
        Assert.Equal(previewUrl, media.GetProperty("derivatives")[0].GetProperty("url").GetString());
        // the original is still behind the Inline ticket, never replaced by the derivative (I3)
        Assert.NotEqual(previewUrl, d.GetProperty("inlineUrl").GetString());

        // an image with alpha gets a PNG thumbnail
        using var thumb = await FetchAsync(previewUrl!);
        Assert.Equal("image/png", thumb.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task The_status_endpoint_lists_the_derivatives_without_urls()
    {
        var owner = await P.CreateUserAsync(starter: false, "status");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(500, 500, SKEncodedImageFormat.Jpeg));
        using var status = await client.GetAsync($"/api/resources/{up.ResourceId}/status");
        var s = await BodyAsync(status);
        var derivative = s.GetProperty("derivatives").EnumerateArray().Single();
        Assert.Equal(("thumb-320", "ready"), (derivative.GetProperty("kind").GetString(), derivative.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, derivative.GetProperty("url").ValueKind);
    }

    // ---- bombs, damaged files ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_decompression_bomb_is_skipped_the_resource_stays_ready_and_the_original_is_served()
    {
        var owner = await P.CreateUserAsync(starter: false, "bomb");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = BombPng(12_000, 12_000); // 144 MP, over Media:MaxPixels
        Assert.True(data.Length < 1_000_000);
        var up = await ReadyAsync(client, data);

        var item = await ListItemAsync(client, up.ResourceId);
        Assert.Equal("ready", item.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("thumbnailUrl").ValueKind);
        using var detail = await client.GetAsync($"/api/resources/{up.ResourceId}");
        var media = (await BodyAsync(detail)).GetProperty("media");
        Assert.Equal((12_000, 12_000), (media.GetProperty("width").GetInt32(), media.GetProperty("height").GetInt32())); // known from the header
        var derivative = media.GetProperty("derivatives").EnumerateArray().Single();
        Assert.Equal("skipped", derivative.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, derivative.GetProperty("url").ValueKind);

        // ARC-011 section 6: above Media:MaxInlinePixels the original is download-only. No inline ticket for the owner or the
        // visitor (the page must not hand a 144 MP image to a browser tab); the download ticket still serves the exact bytes.
        using var inline = await client.PostAsync($"/api/resources/{up.ResourceId}/inline", null);
        Assert.Equal(HttpStatusCode.Conflict, inline.StatusCode);
        using var visitor = api.ApiClient();
        using var descriptor = await visitor.GetAsync($"/api/public/content/{up.Code}");
        var pub = await BodyAsync(descriptor);
        Assert.Equal(JsonValueKind.Null, pub.GetProperty("inlineUrl").ValueKind);
        Assert.Equal(12_000, pub.GetProperty("media").GetProperty("width").GetInt32());
        using var download = await client.PostAsync($"/api/resources/{up.ResourceId}/download", null);
        using var served = await FetchAsync((await BodyAsync(download)).GetProperty("url").GetString()!);
        Assert.Equal(Sha(data), Sha(await served.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task A_tiny_file_declaring_a_huge_image_is_never_decoded_and_stays_download_only()
    {
        // The reported case: 18000 x 18000 (324 MP) in a few KB. The probe reads the header only; the thumbnailer must not create a codec.
        var owner = await P.CreateUserAsync(starter: false, "huge");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = BombPng(18_000, 18_000);
        Assert.True(data.Length < 1_000_000);
        var up = await ReadyAsync(client, data);
        using var detail = await client.GetAsync($"/api/resources/{up.ResourceId}");
        var d = await BodyAsync(detail);
        Assert.Equal("ready", d.GetProperty("state").GetString());
        var media = d.GetProperty("media");
        Assert.Equal((18_000, 18_000), (media.GetProperty("width").GetInt32(), media.GetProperty("height").GetInt32()));
        Assert.Equal("skipped", media.GetProperty("derivatives").EnumerateArray().Single().GetProperty("status").GetString());
        Assert.Equal(1, await ScalarAsync("select count(*) from files.media_derivatives where resource_id = @r and status = 3 and reason = 'too-many-pixels'", ("r", up.ResourceId)));
        using var inline = await client.PostAsync($"/api/resources/{up.ResourceId}/inline", null);
        Assert.Equal(HttpStatusCode.Conflict, inline.StatusCode);
    }

    [Fact]
    public async Task A_download_only_image_downloads_every_time_and_in_parallel_never_503()
    {
        // The reported case: "Download original" of an 18000 x 18000 image sometimes failed with 503. Owner and visitor tickets, minted and
        // fetched many times at once, must all answer 200 with the exact bytes (a lock timeout may only ever hit a burn claim).
        var owner = await P.CreateUserAsync(starter: false, "hugedl");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = BombPng(18_000, 18_000);
        var up = await ReadyAsync(client, data);
        var expected = Sha(data[..4_096]);

        async Task<(HttpStatusCode Mint, HttpStatusCode Get, string Sha)> OneAsync(bool visitor)
        {
            using var visitorClient = visitor ? api.ApiClient() : null;
            var minter = visitorClient ?? client; // the owner client is shared by every task: only the visitor's own client is disposed
            using var mint = visitor
                ? await minter.PostAsync($"/api/public/content/{up.Code}/download", null)
                : await minter.PostAsync($"/api/resources/{up.ResourceId}/download", null);
            if (mint.StatusCode != HttpStatusCode.OK) return (mint.StatusCode, default, "");
            // The test host caps a single response (Content:MaxResponseBytes), so the bytes are read as one Range part, like a download manager.
            var ticket = (await BodyAsync(mint)).GetProperty("url").GetString()!;
            using var host = api.HostClient(FilesApiFactory.UserContentHost);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/d/" + ticket[(ticket.IndexOf("/d/", StringComparison.Ordinal) + 3)..]);
            request.Headers.Range = new RangeHeaderValue(0, 4_095);
            using var served = await host.SendAsync(request);
            return (mint.StatusCode, served.StatusCode == HttpStatusCode.PartialContent ? HttpStatusCode.OK : served.StatusCode, Sha(await served.Content.ReadAsByteArrayAsync()));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => OneAsync(visitor: i % 2 == 0)));
        Assert.All(results, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.Mint);
            Assert.Equal(HttpStatusCode.OK, r.Get);
            Assert.Equal(expected, r.Sha);
        });
    }

    [Fact]
    public async Task An_image_within_the_inline_limit_still_opens_inline()
    {
        var owner = await P.CreateUserAsync(starter: false, "inlineok");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(300, 200, SKEncodedImageFormat.Png));
        using var inline = await client.PostAsync($"/api/resources/{up.ResourceId}/inline", null);
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        using var visitor = api.ApiClient();
        using var descriptor = await visitor.GetAsync($"/api/public/content/{up.Code}");
        Assert.False(string.IsNullOrEmpty((await BodyAsync(descriptor)).GetProperty("inlineUrl").GetString()));
    }

    [Fact]
    public async Task An_undecodable_image_ends_as_no_thumbnail_never_as_an_error()
    {
        var owner = await P.CreateUserAsync(starter: false, "junk");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        // A PNG signature followed by filler: accepted by the sniff and the scan, unreadable by every decoder.
        var data = new byte[3000];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        for (var i = 8; i < data.Length; i++) data[i] = (byte)(i % 251);
        var up = await UploadAsync(client, data);
        await ScanAsync(up.ResourceId);
        await MediaAsync(up.ResourceId); // does not throw: the task is a success

        var item = await ListItemAsync(client, up.ResourceId);
        Assert.Equal("ready", item.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("thumbnailUrl").ValueKind);
        using var detail = await client.GetAsync($"/api/resources/{up.ResourceId}");
        var media = (await BodyAsync(detail)).GetProperty("media");
        Assert.Equal(JsonValueKind.Null, media.GetProperty("width").ValueKind);
        Assert.Equal("failed", media.GetProperty("derivatives")[0].GetProperty("status").GetString());
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM files.media_derivatives WHERE resource_id = @r AND status = 2", ("r", up.ResourceId)));
    }

    [Fact]
    public async Task Running_the_task_twice_is_idempotent()
    {
        var owner = await P.CreateUserAsync(starter: false, "twice");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(640, 480, SKEncodedImageFormat.Png));
        await MediaAsync(up.ResourceId);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM files.media_derivatives WHERE resource_id = @r", ("r", up.ResourceId)));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM files.media_items WHERE resource_id = @r", ("r", up.ResourceId)));
    }

    // ---- served only when the scan is clean --------------------------------------------------------------------

    [Fact]
    public async Task The_media_task_does_nothing_for_an_original_that_is_not_scanned_clean()
    {
        var owner = await P.CreateUserAsync(starter: false, "unscanned");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await UploadAsync(client, Image(300, 300, SKEncodedImageFormat.Png));

        // Not scanned yet: even a hand-queued media task opens nothing and writes nothing.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
            var tasks = scope.ServiceProvider.GetRequiredKeyedService<TaskQueue>(FilesDbContext.Schema);
            await tasks.EnqueueAsync(db, MediaPreviewTaskHandler.TaskKind, $"media-preview:{up.ResourceId}",
                JsonSerializer.Serialize(new { resourceId = up.ResourceId }), 3, DateTime.UtcNow, DateTime.UtcNow, default);
        }
        await MediaAsync(up.ResourceId);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.media_items WHERE resource_id = @r", ("r", up.ResourceId)));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.media_derivatives WHERE resource_id = @r", ("r", up.ResourceId)));
    }

    [Fact]
    public async Task An_infected_upload_gets_no_media_task_and_no_derivative()
    {
        var owner = await P.CreateUserAsync(starter: false, "infected");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var data = Image(300, 300, SKEncodedImageFormat.Png).Concat(Encoding.ASCII.GetBytes(FakeClamdServer.Marker)).ToArray();
        var up = await UploadAsync(client, data);
        await ScanAsync(up.ResourceId);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.tasks WHERE dedupe_key = @k", ("k", $"media-preview:{up.ResourceId}")));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.media_derivatives WHERE resource_id = @r", ("r", up.ResourceId)));
    }

    [Fact]
    public async Task A_derivative_ticket_stops_working_when_the_original_is_not_clean_in_objects()
    {
        var owner = await P.CreateUserAsync(starter: false, "dirty");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(600, 400, SKEncodedImageFormat.Png));
        var thumbUrl = (await ListItemAsync(client, up.ResourceId)).GetProperty("thumbnailUrl").GetString()!;
        using (var ok = await FetchAsync(thumbUrl)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // The object loses its clean verdict (say, a re-scan with new signatures): the derivative is not served either.
        Assert.Equal(1, await ScalarAsync("UPDATE files.storage_objects SET scan_status = 0 WHERE resource_id = @r RETURNING 1", ("r", up.ResourceId)));
        using var gone = await FetchAsync(thumbUrl);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
    }

    [Fact]
    public async Task A_derivative_whose_row_or_file_is_missing_is_gone_not_a_fallback_to_the_original()
    {
        var owner = await P.CreateUserAsync(starter: false, "nofile");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(600, 400, SKEncodedImageFormat.Png));
        var thumbUrl = (await ListItemAsync(client, up.ResourceId)).GetProperty("thumbnailUrl").GetString()!;
        Guid derivativeId;
        await using (var scope = api.Services.CreateAsyncScope())
            derivativeId = (await scope.ServiceProvider.GetRequiredService<IMediaMetadata>().GetAsync([up.ResourceId], default))[up.ResourceId].Derivatives.Single().Id;

        File.Delete(AreaPath(StorageArea.Thumbnails, derivativeId));
        using var response = await FetchAsync(thumbUrl);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task A_preview_ticket_cannot_be_minted_for_a_resource_that_is_not_ready_or_for_a_snippet()
    {
        var owner = await P.CreateUserAsync(starter: false, "mint");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await UploadAsync(client, Image(200, 200, SKEncodedImageFormat.Png)); // still processing
        await using var scope = api.Services.CreateAsyncScope();
        var grants = scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Resources.Contracts.IDownloadGrants>();
        var processing = await grants.MintAsync(new(up.ResourceId, Zaagl.Modules.Resources.Contracts.DeliveryPurpose.Preview, "thumb-320"), default);
        Assert.False(processing.IsSuccess);

        await ScanAsync(up.ResourceId);
        // A preview ticket must name a derivative; an inline ticket must not.
        Assert.False((await grants.MintAsync(new(up.ResourceId, Zaagl.Modules.Resources.Contracts.DeliveryPurpose.Preview, null), default)).IsSuccess);
        Assert.False((await grants.MintAsync(new(up.ResourceId, Zaagl.Modules.Resources.Contracts.DeliveryPurpose.Inline, "thumb-320"), default)).IsSuccess);
        Assert.False((await grants.MintAsync(new(up.ResourceId, Zaagl.Modules.Resources.Contracts.DeliveryPurpose.Preview, "../x"), default)).IsSuccess);
    }

    [Fact]
    public async Task A_preview_ticket_for_a_derivative_that_does_not_exist_is_gone()
    {
        var owner = await P.CreateUserAsync(starter: false, "nokind");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(600, 400, SKEncodedImageFormat.Png));
        await using var scope = api.Services.CreateAsyncScope();
        var minted = await scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Resources.Contracts.IDownloadGrants>()
            .MintAsync(new(up.ResourceId, Zaagl.Modules.Resources.Contracts.DeliveryPurpose.Preview, "preview-1600"), default);
        Assert.True(minted.IsSuccess);
        using var response = await FetchAsync(minted.Value!.Url);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode); // never answers with the original instead
    }

    // ---- lifecycle ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_the_bytes_deletes_the_derivative_and_its_rows()
    {
        var owner = await P.CreateUserAsync(starter: false, "purge");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(700, 700, SKEncodedImageFormat.Png));
        Guid derivativeId;
        await using (var scope = api.Services.CreateAsyncScope())
            derivativeId = (await scope.ServiceProvider.GetRequiredService<IMediaMetadata>().GetAsync([up.ResourceId], default))[up.ResourceId].Derivatives.Single().Id;
        Assert.True(File.Exists(AreaPath(StorageArea.Thumbnails, derivativeId)));

        await using (var scope = api.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IStoredObjects>().QueueDeletionAsync(up.ResourceId, "test", default);
        await RunTaskAsync($"delete:{up.UploadId}");

        Assert.False(File.Exists(AreaPath(StorageArea.Thumbnails, derivativeId)));
        Assert.False(File.Exists(AreaPath(StorageArea.Objects, up.UploadId)));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.media_derivatives WHERE resource_id = @r", ("r", up.ResourceId)));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.media_items WHERE resource_id = @r", ("r", up.ResourceId)));
    }

    [Fact]
    public async Task A_media_task_that_runs_after_the_bytes_were_deleted_makes_nothing()
    {
        var owner = await P.CreateUserAsync(starter: false, "late");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await UploadAsync(client, Image(400, 400, SKEncodedImageFormat.Png));
        await ScanAsync(up.ResourceId);
        await using (var scope = api.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IStoredObjects>().QueueDeletionAsync(up.ResourceId, "test", default);
        await RunTaskAsync($"delete:{up.UploadId}");
        await MediaAsync(up.ResourceId); // the object row is gone: nothing to open, nothing written
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.media_derivatives WHERE resource_id = @r", ("r", up.ResourceId)));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files.media_items WHERE resource_id = @r", ("r", up.ResourceId)));
    }

    [Fact]
    public async Task An_owner_deleted_image_stops_serving_its_thumbnail_at_once()
    {
        var owner = await P.CreateUserAsync(starter: false, "ownerdel");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await ReadyAsync(client, Image(400, 400, SKEncodedImageFormat.Png));
        var thumbUrl = (await ListItemAsync(client, up.ResourceId)).GetProperty("thumbnailUrl").GetString()!;
        using (var ok = await FetchAsync(thumbUrl)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var deleted = await client.DeleteAsync($"/api/resources/{up.ResourceId}")) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await FetchAsync(thumbUrl);
        Assert.NotEqual(HttpStatusCode.OK, gone.StatusCode);
    }
}
