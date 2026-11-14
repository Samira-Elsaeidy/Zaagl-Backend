using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 tests that need real uploaded media: the Files harness (fake clamd, temp storage, direct object serving) plus
/// the media-preview handler in-process, every upload kind, and no read/write throttling.</summary>
public class Phase2MediaFactory : FilesApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        base.ExtraSettings()
            .Where(kv => kv.Key != "Uploads:Kinds")
            .Concat(
            [
                new("Uploads:Kinds", "media,file"),
                new("RateLimits:UnlockPerMinute", "100000"),
                new("RateLimits:CommentWritePerMinute", "100000"),
                new("Guests:PerMinute", "100000"),
            ]);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<MediaConcurrencyGate>();
            services.AddScoped<ITaskHandler<FilesDbContext>, MediaPreviewTaskHandler>();
            services.AddScoped<MediaPreviewTaskHandler>();
            services.AddScoped<DeleteBytesTaskHandler>();
            services.AddScoped<WatermarkTaskHandler>();
        });
    }
}

/// <summary>Upload, scan and media-task helpers (the same real paths as the Phase 1 media tests).</summary>
public static class MediaHelper
{
    public static byte[] Image(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color ?? SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }

    private static string B64(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v));

    /// <summary>tus create, one PATCH, /complete. Returns resource id, code and the link key (null when public).</summary>
    public static async Task<(Guid ResourceId, string Code, string? LinkKey)> UploadAsync(HttpClient client, byte[] data, string filename = "pic.png",
        string kind = "media", bool isPublic = false, DateTime? expiresAt = null, bool burn = false)
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/uploads");
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", data.Length.ToString());
        create.Headers.Add("Upload-Metadata", $"kind {B64(kind)},filename {B64(filename)}");
        using var created = await client.SendAsync(create);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"tus create -> {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        var id = Guid.Parse(created.Headers.Location!.OriginalString["/api/uploads/".Length..]);

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/uploads/{id}") { Content = new ByteArrayContent(data) };
        patch.Headers.Add("Tus-Resumable", "1.0.0");
        patch.Headers.Add("Upload-Offset", "0");
        patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        using var patched = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);

        var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/uploads/{id}/complete")
        {
            Content = JsonContent.Create(new { @public = isPublic, expiresAt, burnAfterDownload = burn }, options: TestPrincipals.Json),
        };
        complete.Headers.Add("Idempotency-Key", "c-" + TestIps.UniqueSuffix());
        using var completed = await client.SendAsync(complete);
        var text = await completed.Content.ReadAsStringAsync();
        Assert.True(completed.StatusCode == HttpStatusCode.Accepted, $"complete -> {(int)completed.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        return (body.GetProperty("resourceId").GetGuid(), body.GetProperty("code").GetString()!,
            body.TryGetProperty("linkKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null);
    }

    public static async Task RunTaskAsync(FilesApiFactory api, string dedupeKey)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.DedupeKey == dedupeKey && t.Status < QueuedTaskStatus.Done);
        var claimed = new ClaimedTask(row.Id, row.Kind, row.DedupeKey, row.Payload, 1, row.MaxAttempts);
        if (row.Kind == ScanTaskHandler.TaskKind) await scope.ServiceProvider.GetRequiredService<ScanTaskHandler>().HandleAsync(claimed, CancellationToken.None);
        else if (row.Kind == MediaPreviewTaskHandler.TaskKind) await scope.ServiceProvider.GetRequiredService<MediaPreviewTaskHandler>().HandleAsync(claimed, CancellationToken.None);
        else if (row.Kind == DeleteBytesTaskHandler.TaskKind) await scope.ServiceProvider.GetRequiredService<DeleteBytesTaskHandler>().HandleAsync(claimed, CancellationToken.None);
        else if (row.Kind == WatermarkTaskHandler.TaskKind) await scope.ServiceProvider.GetRequiredService<WatermarkTaskHandler>().HandleAsync(claimed, CancellationToken.None);
        else throw new InvalidOperationException(row.Kind);
    }

    public static async Task<bool> HasOpenTaskAsync(FilesApiFactory api, string dedupeKey)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        return await db.Tasks.AsNoTracking().AnyAsync(t => t.DedupeKey == dedupeKey && t.Status < QueuedTaskStatus.Done);
    }

    /// <summary>Upload, scan, then the media task when one was queued. The image is ready afterwards.</summary>
    public static async Task<(Guid ResourceId, string Code, string? LinkKey)> ReadyAsync(FilesApiFactory api, HttpClient client, byte[] data,
        string filename = "pic.png", string kind = "media", bool isPublic = false, DateTime? expiresAt = null, bool burn = false)
    {
        var up = await UploadAsync(client, data, filename, kind, isPublic, expiresAt, burn);
        await RunTaskAsync(api, $"scan:{up.ResourceId}");
        if (await HasOpenTaskAsync(api, $"media-preview:{up.ResourceId}")) await RunTaskAsync(api, $"media-preview:{up.ResourceId}");
        return up;
    }

    public static async Task<HttpResponseMessage> FetchAsync(FilesApiFactory api, string ticketUrl, HttpMethod? method = null)
    {
        using var client = api.HostClient(FilesApiFactory.UserContentHost);
        var ticket = ticketUrl[(ticketUrl.IndexOf("/d/", StringComparison.Ordinal) + 3)..];
        return await client.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Get, "/d/" + ticket), HttpCompletionOption.ResponseHeadersRead);
    }
}
