using System.Security.Claims;
using System.Text.Json;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// BE-024 / ARC-014 / contracts C9.2: RFC 8628 device authorization for the CLI and desktop app, plus the device
/// session list. The two anonymous routes speak the RFC 6749 error body (not problem+json); the activation and
/// session routes are the "Interactive" policy (web session JWT only: a device token is refused with 403).
/// </summary>
public static class DeviceEndpoints
{
    public sealed record ActivationBody(string? UserCode, bool Approve);

    public static void MapDeviceFlow(this IEndpointRouteBuilder app)
    {
        var device = app.MapGroup("/api/auth/device").WithTags("Device authorization");

        device.MapPost("/code", async (HttpContext http, DeviceFlowService flow, CancellationToken ct) =>
            {
                NoStore(http);
                var request = await ReadAsync(http, f => new DeviceCodeRequest(f["client_id"].ToString(), Opt(f["device_name"]), Opt(f["os"])), ct);
                var (response, error) = await flow.StartAsync(request, ct);
                return response is not null ? Results.Ok(response) : OAuthError(error!);
            })
            .AllowAnonymous()
            .RequireRateLimiting("device-code");

        device.MapPost("/token", async (HttpContext http, DeviceFlowService flow, CancellationToken ct) =>
            {
                NoStore(http);
                var request = await ReadAsync(http, f => new DeviceTokenRequest(
                    f["grant_type"].ToString(), f["device_code"].ToString(), f["client_id"].ToString()), ct);
                var (response, error) = await flow.RedeemAsync(request, ct);
                return response is not null ? Results.Ok(response) : OAuthError(error!);
            })
            .AllowAnonymous()
            .RequireRateLimiting("device-token");

        device.MapGet("/activation", async (string? userCode, ClaimsPrincipal user, HttpContext http, DeviceFlowService flow, CancellationToken ct) =>
            {
                NoStore(http);
                return ApiResults.From(await flow.GetActivationAsync(user.GetUserId(), userCode, ct));
            })
            .RequireAuthorization("Interactive")
            .RequireRateLimiting("device-activate");

        device.MapPost("/activate", async (ActivationBody body, ClaimsPrincipal user, HttpContext http, DeviceFlowService flow, CancellationToken ct) =>
            {
                NoStore(http);
                return ApiResults.From(await flow.ActivateAsync(user.GetUserId(), body.UserCode, body.Approve, ct), _ => Results.NoContent());
            })
            .RequireAuthorization("Interactive")
            .RequireRateLimiting("device-activate");

        // Device sessions (route names PROPOSED in ARC-014 Q4; built per contracts C9.2).
        var sessions = app.MapGroup("/api/me/sessions").WithTags("Device authorization").RequireAuthorization("Interactive");

        sessions.MapGet("", async (ClaimsPrincipal user, DeviceFlowService flow, CancellationToken ct) =>
            Results.Ok(await flow.ListSessionsAsync(user.GetUserId(), ct)));

        sessions.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, DeviceFlowService flow, CancellationToken ct) =>
            await flow.RevokeSessionAsync(user.GetUserId(), id, ct) ? Results.NoContent() : ApiResults.NotFound("Session not found."));
    }

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static string? Opt(Microsoft.Extensions.Primitives.StringValues v) => v.Count == 0 ? null : v.ToString();

    /// <summary>JSON or form body (RFC 8628 §3.1, §3.4); null when it is missing or malformed.</summary>
    private static async Task<T?> ReadAsync<T>(HttpContext http, Func<IFormCollection, T> fromForm, CancellationToken ct) where T : class
    {
        try
        {
            if (http.Request.HasFormContentType) return fromForm(await http.Request.ReadFormAsync(ct));
            if (http.Request.HasJsonContentType()) return await http.Request.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or BadHttpRequestException) { }
        return null;
    }

    private static IResult OAuthError(DeviceTokenError error) => Results.Json(error, statusCode: StatusCodes.Status400BadRequest);
}
