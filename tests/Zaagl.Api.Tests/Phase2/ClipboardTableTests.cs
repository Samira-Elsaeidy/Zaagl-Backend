using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Collaboration.Application;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.3 clipboard transformers, the pure parts: strict bounded parsing, type inference and the formula-safe CSV export.</summary>
[Trait("Area", "P2-Tables")]
public sealed class TableParserTests
{
    private static readonly TableParseLimits Limits = new(MaxRows: 100, MaxColumns: 10, MaxCellChars: 50, MaxDepth: 4);

    private static ParsedTable Ok(string text, string? format = null, char? delimiter = null, bool header = true)
    {
        var r = TableParser.Parse(text, format, delimiter, header, Limits);
        Assert.True(r.IsSuccess, r.Error?.Message);
        return r.Value!;
    }

    private static string Fail(string text, string? format = null, TableParseLimits? limits = null)
    {
        var r = TableParser.Parse(text, format, null, true, limits ?? Limits);
        Assert.False(r.IsSuccess);
        return r.Error!.Code;
    }

    [Fact]
    public void Json_array_of_objects_unions_keys_and_infers_types()
    {
        var t = Ok("""[{"id":1,"name":"Ann","paid":true,"when":"2026-01-02"},{"id":2.5,"tags":["a"],"when":"2026-01-03T10:00:00Z"}]""");
        Assert.Equal("json", t.SourceFormat);
        Assert.Equal(["id", "name", "paid", "when", "tags"], t.Columns.Select(c => c.Name));
        Assert.Equal(["number", "string", "boolean", "datetime", "json"], t.Columns.Select(c => c.Type));
        Assert.Null(t.Rows[1][1]);
        Assert.Equal("""["a"]""", t.Rows[1][4]!.GetValue<string>());
    }

    [Fact]
    public void Json_shapes_object_arrays_and_scalars()
    {
        var kv = Ok("""{"a":1,"b":"x"}""");
        Assert.Equal(["key", "value"], kv.Columns.Select(c => c.Name));
        var grid = Ok("[[1,2],[3]]");
        Assert.Equal(["column 1", "column 2"], grid.Columns.Select(c => c.Name));
        Assert.Equal("integer", grid.Columns[0].Type);
        var values = Ok("[1,\"a\",null]");
        Assert.Equal("string", Assert.Single(values.Columns).Type);
    }

    [Fact]
    public void Json_is_strict_and_bounded()
    {
        Assert.Equal("validation", Fail("[1,]", "json"));
        Assert.Equal("validation", Fail("[1] // c", "json"));
        Assert.Equal("validation", Fail("""[[[[[[1]]]]]]""", "json")); // depth 6 > 4
        Assert.Equal("validation", Fail("42", "json"));
        Assert.Equal("validation", Fail("[]", "json"));
        Assert.Equal("input-too-large", Fail("[" + string.Join(',', Enumerable.Range(0, 101)) + "]", "json"));
        Assert.Equal("input-too-large", Fail("[{" + string.Join(',', Enumerable.Range(0, 11).Select(i => $"\"k{i}\":1")) + "}]", "json"));
        Assert.Equal("validation", Fail("[\"" + new string('x', 51) + "\"]", "json"));
        Assert.Equal("validation", Fail("[\"a\u0000\"]", "json"));
    }

    [Fact]
    public void Csv_follows_rfc4180_and_detects_the_delimiter()
    {
        var t = Ok("name;amount;note\r\nAnn;12.5;\"semi;colon\"\r\n\"Bo \"\"B\"\"\";-3;\"line\nbreak\"\n\nCy;7\n");
        Assert.Equal("csv", t.SourceFormat);
        Assert.Equal(["name", "amount", "note"], t.Columns.Select(c => c.Name));
        Assert.Equal(["string", "number", "string"], t.Columns.Select(c => c.Type));
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal("Bo \"B\"", t.Rows[1][0]!.GetValue<string>());
        Assert.Equal("line\nbreak", t.Rows[1][2]!.GetValue<string>());
        Assert.Null(t.Rows[2][2]); // short row padded

        var tabs = Ok("a\tb\n1\t2", delimiter: '\t');
        Assert.Equal(["a", "b"], tabs.Columns.Select(c => c.Name));
        var noHeader = Ok("1,2\n3,4", header: false);
        Assert.Equal(["column 1", "column 2"], noHeader.Columns.Select(c => c.Name));
        var dupes = Ok(",x,x\n1,2,3");
        Assert.Equal(["column 1", "x", "x (2)"], dupes.Columns.Select(c => c.Name));
        // Formulas are text: never evaluated.
        var formula = Ok("cell\n=HYPERLINK(\"http://x\")");
        Assert.Equal("=HYPERLINK(\"http://x\")", formula.Rows[0][0]!.GetValue<string>());
    }

    [Fact]
    public void Csv_errors_are_precise()
    {
        Assert.Equal("validation", Fail("a,b\n\"open", "csv"));
        Assert.Equal("validation", Fail("a,b\n1,2,3", "csv"));
        Assert.Equal("x\"y", Ok("a\nx\"y", "csv").Rows[0][0]!.GetValue<string>()); // lenient: a literal quote inside an unquoted cell
        Assert.Equal("validation", Fail("a\n\"x\"y", "csv"));
        Assert.Equal("input-too-large", Fail("a\n" + string.Join('\n', Enumerable.Range(0, 101)), "csv"));
        Assert.Equal("input-too-large", Fail(string.Join(',', Enumerable.Range(0, 11)), "csv"));
        Assert.Equal("validation", Fail("a\n" + new string('x', 51), "csv"));
        Assert.Equal("validation", Fail("   ", "csv"));
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3+cmd|' /C calc'!A0", "'-2+3+cmd|' /C calc'!A0")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "\"'\tx\"")]
    [InlineData("\rx", "\"'\rx\"")]
    [InlineData("-12.5", "-12.5")]
    [InlineData("+3", "+3")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Csv_export_neutralises_formulas(string cell, string expected) => Assert.Equal(expected, TableExport.Field(cell));

    [Fact]
    public void Exports_round_trip_the_table()
    {
        var t = Ok("""[{"a":"=x","b":2,"c":null,"d":false}]""");
        var csv = TableExport.Csv(t.Columns, t.Rows);
        Assert.Equal("a,b,c,d\r\n'=x,2,,false\r\n", csv);
        var json = JsonNode.Parse(TableExport.Json(t.Columns, t.Rows))!.AsArray();
        Assert.Equal("=x", json[0]!["a"]!.GetValue<string>()); // JSON keeps the value; only CSV is neutralised
        Assert.Equal(2, json[0]!["b"]!.GetValue<int>());
    }
}

/// <summary>Phase 2 E2.3 clipboard tables over HTTP: guest and account creation, gate, paging, exports, plan caps, owner-only routes,
/// expiry and purge.</summary>
[Trait("Area", "P2-Tables")]
public sealed class ClipboardTableTests(Phase2Factory api) : IClassFixture<Phase2Factory>
{
    private TestPrincipals P => api.Principals;

    [Fact]
    public async Task An_account_creates_a_table_link_and_a_visitor_pages_and_exports_it()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var csv = "item,qty,price\n" + string.Join('\n', Enumerable.Range(1, 30).Select(i => $"=cmd{i},{i},{i}.5"));
        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/tables", new { text = csv, title = "Q3 order" });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        var key = created.GetProperty("linkKey").GetString();

        using var visitor = api.ApiClient();
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}")).Status);
        using (var raw = await P2.RawAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}?offset=10&limit=5", headers: P2.Key(key)))
        {
            Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
            Assert.True(raw.Headers.CacheControl!.NoStore && raw.Headers.CacheControl.Private);
            var desc = JsonDocument.Parse(await raw.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("table", desc.GetProperty("kind").GetString());
            Assert.Equal(30, desc.GetProperty("rowCount").GetInt32());
            Assert.Equal(5, desc.GetProperty("count").GetInt32());
            Assert.Equal(["string", "integer", "number"], desc.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("type").GetString()));
            Assert.Equal("=cmd11", desc.GetProperty("rows")[0][0].GetString()); // data, as typed
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}?limit=5001", headers: P2.Key(key))).Status);

        var (exported, file) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}/export?format=csv", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.OK, exported);
        Assert.Equal("Q3-order.csv", file.GetProperty("fileName").GetString());
        Assert.Equal("text/csv", file.GetProperty("mime").GetString());
        Assert.StartsWith("item,qty,price\r\n'=cmd1,1,1.5\r\n", file.GetProperty("content").GetString());
        var (_, jsonFile) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}/export?format=json", headers: P2.Key(key));
        Assert.Equal("=cmd1", JsonDocument.Parse(jsonFile.GetProperty("content").GetString()!).RootElement[0].GetProperty("item").GetString());

        using var shortClient = api.ShortClient();
        using var redirect = await shortClient.GetAsync($"/{created.Code()}");
        Assert.Equal($"{ZaaglApiFactory.WebBase}/t/{created.Code()}", redirect.Headers.Location!.ToString());

        // Owner view, export, library, delete.
        var id = created.ResourceId();
        var (_, owned) = await P2.SendAsync(client, HttpMethod.Get, $"/api/tables/{id}?limit=2");
        Assert.Equal(2, owned.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await P2.SendAsync(client, HttpMethod.Get, $"/api/tables/{id}/export?format=json")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(client, HttpMethod.Get, $"/api/tables/{id}/export?format=xlsx")).Status);
        var (_, library) = await P2.SendAsync(client, HttpMethod.Get, "/api/resources?kind=table");
        Assert.Contains(library.GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);

        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Get, $"/api/tables/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Get, $"/api/tables/{id}/export")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Delete, $"/api/tables/{id}")).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/tables/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}", headers: P2.Key(key))).Status);
    }

    [Fact]
    public async Task Guests_create_with_the_panel_gates_and_never_public()
    {
        using var guest = api.ApiClient();
        var (status, created) = await P2.SendAsync(guest, HttpMethod.Post, "/api/tables", new { text = """[{"a":1}]""" });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        Assert.NotNull(created.GetProperty("linkKey").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(guest, HttpMethod.Post, "/api/tables", new { text = "[1]", @public = true })).Status);
        // Guest cap: 64 KB of input, 500 rows.
        var (big, bigBody) = await P2.SendAsync(guest, HttpMethod.Post, "/api/tables", new { text = "[" + string.Join(',', Enumerable.Range(0, 501)) + "]" });
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "input-too-large"), (big, bigBody.ProblemCode()));
        var (huge, hugeBody) = await P2.SendAsync(guest, HttpMethod.Post, "/api/tables", new { text = new string('a', 65 * 1024) });
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "input-too-large"), (huge, hugeBody.ProblemCode()));
        // A failing credential is never a guest.
        using var broken = api.ApiClient(bearer: "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(broken, HttpMethod.Post, "/api/tables", new { text = "[1]" })).Status);
        // An API key needs tools:write.
        var user = await P.CreateUserAsync(starter: true);
        var readOnly = await P.CreateApiKeyAsync(user, "resources:read");
        using var keyClient = api.ApiClient(apiKey: readOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await P2.SendAsync(keyClient, HttpMethod.Post, "/api/tables", new { text = "[1]" })).Status);
        var writer = await P.CreateApiKeyAsync(user, "tools:write");
        using var writerClient = api.ApiClient(apiKey: writer);
        Assert.Equal(HttpStatusCode.Created, (await P2.SendAsync(writerClient, HttpMethod.Post, "/api/tables", new { text = "[1]" })).Status);
    }

    [Fact]
    public async Task Validation_plans_expiry_and_purge()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        foreach (var bad in new object[]
                 {
                     new { }, new { text = "" }, new { text = "[1]", format = "xml" }, new { text = "a,b", delimiter = "#" },
                     new { text = "[1]", title = "t\u0001" }, new { text = "{\"a\":}" },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(client, HttpMethod.Post, "/api/tables", bad)).Status);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(client, HttpMethod.Post, "/api/tables", new { text = "[1]", password = "secret1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(client, HttpMethod.Post, "/api/tables",
            new { text = "[1]", expiresAt = DateTime.UtcNow.AddDays(8) })).Status);

        var (_, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/tables", new { text = "a\n1", @public = true, expiresAt = DateTime.UtcNow.AddHours(1) });
        using var visitor = api.ApiClient();
        Assert.Equal(HttpStatusCode.OK, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}")).Status);
        api.Clock.Advance(TimeSpan.FromHours(2));
        try
        {
            Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}")).Status);
            Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/tables/{created.Code()}/export")).Status);
        }
        finally
        {
            api.Clock.Advance(-TimeSpan.FromHours(2));
        }

        // Purge (Resources reclaims the resource): the row goes.
        await using var scope = api.Services.CreateAsyncScope();
        foreach (var handler in scope.ServiceProvider.GetServices<Zaagl.Modules.Resources.Contracts.IResourceKindHandler>()
                     .Where(h => h.Kind == Zaagl.Modules.Resources.Contracts.ResourceKind.Table))
            await handler.OnResourceDeletedAsync(created.ResourceId(), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Collaboration.Infrastructure.CollabDbContext>();
        Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Tables, t => t.ResourceId == created.ResourceId()));
    }
}
