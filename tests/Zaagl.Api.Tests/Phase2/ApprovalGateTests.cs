using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Files.Contracts;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.2 watermark and approval gate (docs/phase2/approval-gate.md): the original is never reachable before release,
/// the watermark is burned into a separate derivative, approval is atomic and recorded, the release rules and the payment seam.</summary>
[Trait("Area", "P2-Approval")]
public sealed class ApprovalGateTests(Phase2MediaFactory api) : IClassFixture<Phase2MediaFactory>
{
    private TestPrincipals P => api.Principals;
    private static readonly SKColor Base = new(40, 120, 200);

    private async Task<(HttpClient Owner, Guid DraftId, byte[] Original, JsonElement Created)> ApprovalAsync(object? extra = null)
    {
        var user = await P.CreateUserAsync(starter: true);
        var owner = api.ApiClient(bearer: user.AccessToken);
        var original = MediaHelper.Image(800, 600, color: Base);
        var draft = await MediaHelper.ReadyAsync(api, owner, original, "design.png");
        var body = new Dictionary<string, object?> { ["draftResourceId"] = draft.ResourceId, ["watermarkText"] = "DRAFT - ACME", ["title"] = "Logo v3" };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var (status, created) = await P2.SendAsync(owner, HttpMethod.Post, "/api/approvals", body);
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        return (owner, draft.ResourceId, original, created);
    }

    private Task RenderAsync(Guid draftId)
    {
        using var scope = api.Services.CreateScope();
        var kind = scope.ServiceProvider.GetRequiredService<IWatermarkDerivatives>().KindFor("DRAFT - ACME");
        return MediaHelper.RunTaskAsync(api, $"watermark:{draftId}:{kind}");
    }

    [Fact]
    public async Task Before_release_only_the_watermarked_derivative_is_served()
    {
        var (_, draftId, original, created) = await ApprovalAsync();
        var code = created.Code();
        var key = created.GetProperty("linkKey").GetString();
        using var client = api.ApiClient();

        var (_, before) = await P2.SendAsync(client, HttpMethod.Get, $"/api/public/approvals/{code}", headers: P2.Key(key));
        Assert.Equal("processing", before.GetProperty("previewState").GetString());
        Assert.Equal("draft", before.GetProperty("approval").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("original").ValueKind);
        Assert.True(before.GetProperty("canApprove").GetBoolean());

        await RenderAsync(draftId);
        var (_, desc) = await P2.SendAsync(client, HttpMethod.Get, $"/api/public/approvals/{code}", headers: P2.Key(key));
        Assert.Equal("ready", desc.GetProperty("previewState").GetString());
        Assert.Equal(JsonValueKind.Null, desc.GetProperty("original").ValueKind);
        var preview = desc.GetProperty("preview");
        Assert.Equal("image/webp", preview.GetProperty("mime").GetString());

        using var served = await MediaHelper.FetchAsync(api, preview.GetProperty("inlineUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType!.MediaType);
        var bytes = await served.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(original, bytes);
        using var bitmap = SKBitmap.Decode(bytes);
        var changed = 0;
        var total = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                total++;
                var c = bitmap.GetPixel(x, y);
                if (Math.Abs(c.Red - Base.Red) + Math.Abs(c.Green - Base.Green) + Math.Abs(c.Blue - Base.Blue) > 30) changed++;
            }
        Assert.True(changed > total / 20, $"only {changed} of {total} sampled pixels carry the watermark");

        // The original cannot be downloaded before release.
        var (download, downloadBody) = await P2.SendAsync(client, HttpMethod.Post, $"/api/public/approvals/{code}/download", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.Forbidden, download);
        Assert.Equal("forbidden", downloadBody.ProblemCode());

        using var shortClient = api.ShortClient();
        using var redirect = await shortClient.GetAsync($"/{code}");
        Assert.Equal($"{ZaaglApiFactory.WebBase}/a/{code}", redirect.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Approval_is_recorded_once_and_releases_the_untouched_original()
    {
        var (owner, draftId, original, created) = await ApprovalAsync();
        await RenderAsync(draftId);
        var code = created.Code();
        var key = created.GetProperty("linkKey").GetString();

        // Validation first.
        using var bad = api.ApiClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(bad, HttpMethod.Post, $"/api/public/approvals/{code}/approve", new { email = "not-an-email" }, P2.Key(key))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(bad, HttpMethod.Post, $"/api/public/approvals/{code}/approve", new { name = "x" })).Status);

        // Many parallel approvals: one transition, every caller sees the same record.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var c = api.ApiClient();
            return await P2.SendAsync(c, HttpMethod.Post, $"/api/public/approvals/{code}/approve",
                new { name = $"Client {i}", email = $"client{i}@example.test", note = "Looks great" }, P2.Key(key));
        }));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Single(results.Select(r => r.Body.GetProperty("approvedAt").GetDateTime()).Distinct());
        Assert.Single(results.Select(r => r.Body.GetProperty("approverName").GetString()).Distinct());
        Assert.All(results, r => Assert.Equal("released", r.Body.GetProperty("state").GetString()));

        using var visitor = api.ApiClient();
        var (_, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/approvals/{code}", headers: P2.Key(key));
        Assert.Equal(JsonValueKind.Null, desc.GetProperty("preview").ValueKind);
        Assert.False(desc.GetProperty("canApprove").GetBoolean());
        using (var inline = await MediaHelper.FetchAsync(api, desc.GetProperty("original").GetProperty("inlineUrl").GetString()!))
            Assert.Equal(original, await inline.Content.ReadAsByteArrayAsync());
        var (dl, ticket) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/approvals/{code}/download", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.OK, dl);
        using (var file = await MediaHelper.FetchAsync(api, ticket.GetProperty("url").GetString()!))
        {
            Assert.StartsWith("attachment", file.Content.Headers.ContentDisposition!.ToString());
            Assert.Equal(original, await file.Content.ReadAsByteArrayAsync());
        }

        var (_, owned) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/approvals/{created.ResourceId()}");
        Assert.Equal("released", owned.GetProperty("approval").GetProperty("state").GetString());
        Assert.Matches("^client[0-7]@example.test$", owned.GetProperty("approverEmail").GetString()!);
        Assert.Equal("Looks great", owned.GetProperty("approvalNote").GetString());
    }

    [Fact]
    public async Task Manual_release_and_the_payment_seam_follow_the_state_machine()
    {
        var (owner, draftId, _, created) = await ApprovalAsync(new { releaseOnApproval = false });
        var id = created.ResourceId();
        var code = created.Code();
        var key = created.GetProperty("linkKey").GetString();
        await RenderAsync(draftId);

        Assert.Equal(HttpStatusCode.Conflict, (await P2.SendAsync(owner, HttpMethod.Post, $"/api/approvals/{id}/release")).Status); // still a draft
        using var visitor = api.ApiClient();
        var (_, approved) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/approvals/{code}/approve", new { }, P2.Key(key));
        Assert.Equal("approved", approved.GetProperty("state").GetString());
        var (_, stillDraft) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/approvals/{code}", headers: P2.Key(key));
        Assert.Equal(JsonValueKind.Null, stillDraft.GetProperty("original").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/approvals/{code}/download", headers: P2.Key(key))).Status);

        // The payment seam (no provider is built): approved -> paid, idempotent per reference, then the owner releases.
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ApprovalService>();
            var paid = await service.MarkPaidAsync(id, "test", "pay_123", CancellationToken.None);
            Assert.Equal("paid", paid.Value!.State);
            Assert.Equal("paid", (await service.MarkPaidAsync(id, "test", "pay_123", CancellationToken.None)).Value!.State);
        }
        var (released, info) = await P2.SendAsync(owner, HttpMethod.Post, $"/api/approvals/{id}/release");
        Assert.Equal(HttpStatusCode.OK, released);
        Assert.Equal("released", info.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/approvals/{code}/download", headers: P2.Key(key))).Status);
    }

    [Fact]
    public void The_state_machine_is_total_and_never_releases_a_draft()
    {
        Assert.Equal(ApprovalState.Approved, ApprovalStateMachine.Next(ApprovalState.Draft, ApprovalEvent.Approve));
        Assert.Equal(ApprovalState.Paid, ApprovalStateMachine.Next(ApprovalState.Draft, ApprovalEvent.Pay));
        Assert.Equal(ApprovalState.Paid, ApprovalStateMachine.Next(ApprovalState.Approved, ApprovalEvent.Pay));
        Assert.Equal(ApprovalState.Released, ApprovalStateMachine.Next(ApprovalState.Approved, ApprovalEvent.Release));
        Assert.Equal(ApprovalState.Released, ApprovalStateMachine.Next(ApprovalState.Paid, ApprovalEvent.Release));
        Assert.Null(ApprovalStateMachine.Next(ApprovalState.Draft, ApprovalEvent.Release));
        Assert.Null(ApprovalStateMachine.Next(ApprovalState.Released, ApprovalEvent.Approve));
        Assert.Null(ApprovalStateMachine.Next(ApprovalState.Paid, ApprovalEvent.Approve));
        Assert.Null(ApprovalStateMachine.Next(ApprovalState.Released, ApprovalEvent.Pay));
    }

    [Fact]
    public async Task Plan_ownership_and_owner_routes()
    {
        var free = await P.CreateUserAsync(starter: false);
        using var f = api.ApiClient(bearer: free.AccessToken);
        var image = await MediaHelper.ReadyAsync(api, f, MediaHelper.Image(40, 40), "f.png");
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(f, HttpMethod.Post, "/api/approvals", new { draftResourceId = image.ResourceId })).Status);

        var (owner, _, _, created) = await ApprovalAsync();
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await P2.SendAsync(m, HttpMethod.Post, "/api/approvals", new { draftResourceId = image.ResourceId })).Status);
        var id = created.ResourceId();
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Get, $"/api/approvals/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Post, $"/api/approvals/{id}/release")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Delete, $"/api/approvals/{id}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(owner, HttpMethod.Post, "/api/approvals",
            new { draftResourceId = Guid.NewGuid(), watermarkText = new string('w', 61) })).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(owner, HttpMethod.Delete, $"/api/approvals/{id}")).StatusCode);
        using var visitor = api.ApiClient();
        Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/approvals/{created.Code()}/approve", new { },
            P2.Key(created.GetProperty("linkKey").GetString()))).Status);
    }
}
