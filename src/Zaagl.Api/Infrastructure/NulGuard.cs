using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// PostgreSQL text cannot hold U+0000 (SQLSTATE 22021), so a NUL that reached a query or an insert surfaced as an
/// unhandled 500 (link titles, folder names, <c>?search=%00</c>). Refusing it at the edge turns every such request into
/// a clean 400 instead of fixing each field one by one.
/// </summary>
public sealed class NulRejectingStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is not null && value.Contains('\0'))
            throw new JsonException("Text fields cannot contain NUL (\\u0000) characters.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public static class NulGuard
{
    /// <summary>A NUL in the query string or path (raw or percent-encoded) is answered with 400 before routing.</summary>
    public static IApplicationBuilder UseNulGuard(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var query = http.Request.QueryString.Value ?? "";
        var path = http.Request.Path.Value ?? "";
        if (query.Contains("%00", StringComparison.Ordinal) || query.Contains('\0') || path.Contains('\0'))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Requests cannot contain NUL (\\u0000) characters.",
                extensions: new Dictionary<string, object?> { ["code"] = Zaagl.SharedKernel.ErrorCodes.InvalidRequest }).ExecuteAsync(http);
            return;
        }
        await next();
    });
}
