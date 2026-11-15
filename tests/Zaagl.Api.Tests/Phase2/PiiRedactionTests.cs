using System.Net;
using System.Text.Json;
using SkiaSharp;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Files.Application;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.3 PII redaction guard, the pure parts: the text check (Luhn cards, emails, phones, documented key patterns) and
/// the renderer (pixels inside the rectangles are gone, the rest untouched).</summary>
[Trait("Area", "P2-Redaction")]
public sealed class PiiDetectorTests
{
    private static string[] Kinds(string text) => PiiDetector.Scan(text).Select(s => s.Kind).ToArray();

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("5555555555554444", true)]
    [InlineData("378282246310005", true)]  // Amex, 15 digits
    [InlineData("4111 1111 1111 1112", false)] // Luhn fails
    [InlineData("0000000000000000", false)]   // all the same digit
    [InlineData("411111111111", false)]      // 12 digits
    public void Cards_need_13_to_19_digits_and_a_valid_Luhn_checksum(string text, bool card) =>
        Assert.Equal(card, Kinds($"pay with {text} today").Contains(PiiDetector.Card));

    [Fact]
    public void Luhn_matches_the_reference_algorithm()
    {
        Assert.True(PiiDetector.Luhn([7, 9, 9, 2, 7, 3, 9, 8, 7, 1, 3]));
        Assert.False(PiiDetector.Luhn([7, 9, 9, 2, 7, 3, 9, 8, 7, 1, 4]));
    }

    [Theory]
    [InlineData("mail ann.lee+work@example.co.uk now", "email")]
    [InlineData("call +44 20 7946 0958", "phone")]
    [InlineData("call (555) 123-4567", "phone")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE", "aws-access-key")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "github-token")]
    [InlineData("xoxb-1234567890-abcdefghij", "slack-token")]
    [InlineData("sk_" + "live_4eC39HqLyjWDarjtT1zdp7dc", "stripe-key")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuv", "google-api-key")]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwxyz12", "openai-key")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "jwt")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", "private-key")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234567890", "bearer-token")]
    [InlineData("api_key = \"s3cr3t-value-123\"", "secret-assignment")]
    public void Each_documented_kind_is_found(string text, string kind) => Assert.Contains(kind, Kinds(text));

    [Fact]
    public void Plain_text_dates_and_short_numbers_are_not_flagged()
    {
        Assert.Empty(PiiDetector.Scan("Meeting on 2026-11-15 at 10:30, room 42, total 1250 units."));
        var spans = PiiDetector.Scan("x ann@example.com y");
        Assert.Equal(("email", 2, 15), (spans[0].Kind, spans[0].Start, spans[0].Length));
    }

    [Fact]
    public void Ocr_words_split_across_boxes_are_joined_per_line()
    {
        var words = new[]
        {
            new OcrWord("Card:", 0.1, 0.1, 0.05, 0.02, 0), new OcrWord("4111", 0.16, 0.1, 0.04, 0.02, 0), new OcrWord("1111", 0.21, 0.1, 0.04, 0.02, 0),
            new OcrWord("1111", 0.26, 0.1, 0.04, 0.02, 0), new OcrWord("1111", 0.31, 0.1, 0.04, 0.02, 0),
            new OcrWord("bob@example.com", 0.1, 0.2, 0.15, 0.02, 1), new OcrWord("hello", 0.3, 0.2, 0.05, 0.02, 1),
        };
        var boxes = PiiDetector.Boxes(words);
        Assert.Equal([1, 2, 3, 4], boxes.Where(b => b.Kind == "card").Select(b => b.Word));
        Assert.Equal(5, Assert.Single(boxes, b => b.Kind == "email").Word);
        Assert.All(boxes, b => Assert.True(b.X >= 0 && b.Y >= 0 && b.X + b.W <= 1 && b.Y + b.H <= 1));
    }

    [Fact]
    public void The_renderer_paints_only_inside_the_regions()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(100, 50, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var c = new SKCanvas(bitmap)) c.Clear(SKColors.White);
        RedactionRenderer.Apply(bitmap, [new RedactionRegion(0.1, 0.2, 0.3, 0.4)], RedactionStyles.Solid);
        Assert.Equal(SKColors.Black, bitmap.GetPixel(10, 10));   // first pixel of the region (x 10..40, y 10..30)
        Assert.Equal(SKColors.Black, bitmap.GetPixel(39, 29));
        Assert.Equal(SKColors.White, bitmap.GetPixel(9, 10));
        Assert.Equal(SKColors.White, bitmap.GetPixel(40, 30));

        using var noisy = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) noisy.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);
        RedactionRenderer.Apply(noisy, [new RedactionRegion(0, 0, 1, 1)], RedactionStyles.Pixelate);
        Assert.Equal(noisy.GetPixel(0, 0), noisy.GetPixel(11, 11)); // a mosaic block of at least 12 px is one colour
    }
}

/// <summary>Phase 2 E2.3 PII redaction guard over HTTP: the redacted copy is a new scanned image with its own link, the original is deleted
/// by default, only the owner's own images can be redacted, plan caps, the text check endpoint.</summary>
[Trait("Area", "P2-Redaction")]
public sealed class PiiRedactionTests(Phase2MediaFactory api) : IClassFixture<Phase2MediaFactory>
{
    private TestPrincipals P => api.Principals;

    [Fact]
    public async Task The_redacted_copy_is_a_new_scanned_image_and_the_original_is_deleted()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var original = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(200, 100, color: SKColors.White), "screenshot.png");

        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/redactions", new
        {
            sourceResourceId = original.ResourceId,
            regions = new[] { new { x = 0.5, y = 0.5, w = 0.25, h = 0.5 } },
            title = "Bug report",
        });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        Assert.True(created.GetProperty("originalDeleted").GetBoolean());
        Assert.Equal("processing", created.GetProperty("status").GetString());
        Assert.Equal(200, created.GetProperty("width").GetInt32());
        var id = created.ResourceId();
        Assert.NotEqual(original.ResourceId, id);

        // The original's link is gone for everyone.
        using var visitor = api.ApiClient();
        var (gone, _) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/content/{original.Code}", headers: P2.Key(original.LinkKey));
        Assert.Equal(HttpStatusCode.Gone, gone);

        // The copy goes through the normal scan, then is served like any image of the owner.
        await MediaHelper.RunTaskAsync(api, $"scan:{id}");
        if (await MediaHelper.HasOpenTaskAsync(api, $"media-preview:{id}")) await MediaHelper.RunTaskAsync(api, $"media-preview:{id}");
        var (_, resource) = await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{id}");
        Assert.Equal("ready", resource.GetProperty("state").GetString());
        Assert.Equal("Bug report", resource.GetProperty("title").GetString());
        var (ok, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/content/{created.Code()}", headers: P2.Key(created.GetProperty("linkKey").GetString()));
        Assert.Equal(HttpStatusCode.OK, ok);
        using var bytes = await MediaHelper.FetchAsync(api, desc.GetProperty("inlineUrl").GetString()!);
        Assert.Equal("image/png", bytes.Content.Headers.ContentType!.MediaType);
        using var decoded = SKBitmap.Decode(await bytes.Content.ReadAsByteArrayAsync());
        Assert.Equal(SKColors.Black, decoded.GetPixel(100, 50));  // inside (x 100..150, y 50..100)
        Assert.Equal(SKColors.Black, decoded.GetPixel(149, 99));
        Assert.Equal(SKColors.White, decoded.GetPixel(99, 50));   // outside
        Assert.Equal(SKColors.White, decoded.GetPixel(150, 10));
    }

    [Fact]
    public async Task Keeping_the_original_pixelating_and_the_rules()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var original = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(60, 60), "s.png");
        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/redactions", new
        {
            sourceResourceId = original.ResourceId, regions = new[] { new { x = 0, y = 0, w = 1, h = 0.5 } }, style = "pixelate", deleteOriginal = false,
        });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        Assert.False(created.GetProperty("originalDeleted").GetBoolean());
        Assert.Equal("ready", (await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{original.ResourceId}")).Body.GetProperty("state").GetString());

        async Task<(HttpStatusCode, string)> Redact(object body)
        {
            var (s, b) = await P2.SendAsync(client, HttpMethod.Post, "/api/redactions", body);
            return (s, b.ValueKind == JsonValueKind.Object ? b.ProblemCode() : "");
        }
        var one = new[] { new { x = 0.1, y = 0.1, w = 0.1, h = 0.1 } };
        foreach (var bad in new object[]
                 {
                     new { sourceResourceId = original.ResourceId },
                     new { sourceResourceId = original.ResourceId, regions = new[] { new { x = 0.9, y = 0.1, w = 0.2, h = 0.1 } } },
                     new { sourceResourceId = original.ResourceId, regions = new[] { new { x = 0.1, y = 0.1, w = 0.0, h = 0.1 } } },
                     new { sourceResourceId = original.ResourceId, regions = one, style = "blur" },
                     new { sourceResourceId = original.ResourceId, regions = one, fill = "red" },
                     new { sourceResourceId = original.ResourceId, regions = Enumerable.Range(0, 51).Select(_ => new { x = 0.1, y = 0.1, w = 0.1, h = 0.1 }).ToArray() },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await Redact(bad)).Item1);

        // Only the owner's own ready images: a file, someone else's image or a missing id are the same 422.
        var file = await MediaHelper.ReadyAsync(api, client, "%PDF-1.4 x"u8.ToArray(), "x.pdf", kind: "file");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Redact(new { sourceResourceId = file.ResourceId, regions = one }));
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        var (mStatus, mBody) = await P2.SendAsync(m, HttpMethod.Post, "/api/redactions", new { sourceResourceId = original.ResourceId, regions = one, deleteOriginal = true });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), (mStatus, mBody.ProblemCode()));
        Assert.Equal("ready", (await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{original.ResourceId}")).Body.GetProperty("state").GetString());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Redact(new { sourceResourceId = Guid.NewGuid(), regions = one }));

        using var anonymous = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(anonymous, HttpMethod.Post, "/api/redactions", new { sourceResourceId = original.ResourceId, regions = one })).Status);
        var starter = await P.CreateUserAsync(starter: true, tag: "keys"); // API keys are a Starter capability (PD-07)
        var readKey = await P.CreateApiKeyAsync(starter, "resources:read");
        using var keyClient = api.ApiClient(apiKey: readKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await P2.SendAsync(keyClient, HttpMethod.Post, "/api/redactions", new { sourceResourceId = original.ResourceId, regions = one })).Status);
    }

    [Fact]
    public async Task The_text_check_finds_pii_without_echoing_it()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var (ok, body) = await P2.SendAsync(client, HttpMethod.Post, "/api/redactions/detect", new { text = "card 4111 1111 1111 1111, mail a@b.io" });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(["card", "email"], body.GetProperty("spans").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));
        Assert.DoesNotContain("4111", body.ToString());

        var (boxesOk, boxes) = await P2.SendAsync(client, HttpMethod.Post, "/api/redactions/detect", new
        {
            words = new[] { new { text = "token", x = 0.1, y = 0.1, w = 0.1, h = 0.05, line = 0 }, new { text = "ghp_abcdefghijklmnopqrstuvwxyz0123456789", x = 0.25, y = 0.1, w = 0.5, h = 0.05, line = 0 } },
        });
        Assert.Equal(HttpStatusCode.OK, boxesOk);
        Assert.Equal(1, Assert.Single(boxes.GetProperty("boxes").EnumerateArray()).GetProperty("word").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(client, HttpMethod.Post, "/api/redactions/detect", new { })).Status);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await P2.SendAsync(client, HttpMethod.Post, "/api/redactions/detect", new { text = new string('x', 65 * 1024 + 1) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(client, HttpMethod.Post, "/api/redactions/detect",
            new { words = new[] { new { text = "x", x = 0.9, y = 0.1, w = 0.2, h = 0.1, line = 0 } } })).Status);
    }
}
