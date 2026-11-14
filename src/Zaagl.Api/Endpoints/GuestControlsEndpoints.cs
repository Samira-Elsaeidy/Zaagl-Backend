using System.Security.Claims;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// The CAPTCHA / guest-creation control panel (PHASE1_STATUS "CAPTCHA admin control panel", the last Phase 1 item):
/// run-time guest settings per feature (pause, CAPTCHA required) and the guest limits, a one-click pause/resume of all
/// guest creation, guest stats and the audit trail. Everything is under /api/admin, so it needs what every admin route
/// needs (role Admin, a JWT issued after TOTP, Admin:AllowedNetworks, no native token; spec §7.4) and is rate limited per
/// admin ("admin" policy). Every change writes an audit row (who, when, old, new) and a warning log line.
/// <c>GET /api/guest-status</c> is the public read the website uses before it shows a guest form.
/// </summary>
public static class GuestControlsEndpoints
{
    public static void MapGuestControls(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/guest-controls").WithTags("Admin").RequireAuthorization("Admin").RequireRateLimiting("admin");

        group.MapGet("", async (HttpContext http, GuestSettingsStore store, CancellationToken ct) =>
        {
            var settings = await store.GetAsync(ct);
            if (settings is null) return ApiResults.Problem(Error.ServiceUnavailable("The guest settings could not be read. Try again shortly."));
            return Results.Ok(Describe(http, settings, store));
        });

        group.MapPut("", async (UpdateGuestSettingsRequest request, ClaimsPrincipal caller, HttpContext http,
                GuestControlService service, GuestSettingsStore store, CancellationToken ct) =>
            {
                var saved = await service.UpdateAsync(request, caller.GetUserId(), Email(caller), ct);
                return saved.IsSuccess ? Results.Ok(Describe(http, saved.Value!, store)) : ApiResults.Problem(saved.Error!);
            });

        group.MapPost("/pause-all", async (GuestPauseRequest? request, ClaimsPrincipal caller, HttpContext http,
                GuestControlService service, GuestSettingsStore store, CancellationToken ct) =>
            {
                var saved = await service.PauseAllAsync(caller.GetUserId(), Email(caller), request?.Reason, ct);
                return saved.IsSuccess ? Results.Ok(Describe(http, saved.Value!, store)) : ApiResults.Problem(saved.Error!);
            });

        group.MapPost("/resume-all", async (GuestPauseRequest? request, ClaimsPrincipal caller, HttpContext http,
                GuestControlService service, GuestSettingsStore store, CancellationToken ct) =>
            {
                var saved = await service.ResumeAllAsync(caller.GetUserId(), Email(caller), request?.Reason, ct);
                return saved.IsSuccess ? Results.Ok(Describe(http, saved.Value!, store)) : ApiResults.Problem(saved.Error!);
            });

        group.MapGet("/stats", async (GuestSettingsStore store, GuestControlService service, CancellationToken ct) =>
        {
            var settings = await store.GetAsync(ct);
            if (settings is null) return ApiResults.Problem(Error.ServiceUnavailable("The guest settings could not be read. Try again shortly."));
            return Results.Ok(await service.StatsAsync(settings, ct));
        });

        group.MapGet("/audit", async (GuestControlService service, CancellationToken ct, int limit = 50) =>
            Results.Ok(await service.AuditAsync(limit, ct)));

        // Public: whether each guest feature is open and needs a CAPTCHA, so the website can say "paused" before the visitor
        // writes anything, and skip the widget when an admin turned CAPTCHA off. Holds nothing secret (the same facts a
        // create attempt reveals). The configuration kill switches are folded in.
        app.MapGet("/api/guest-status", async (GuestSettingsStore store, IOptions<GuestOptions> guestOptions,
                IOptions<UploadOptions> uploadOptions, HttpContext http, CancellationToken ct) =>
            {
                var settings = await store.GetAsync(ct);
                if (settings is null) return ApiResults.Problem(Error.ServiceUnavailable("Guest creation is unavailable right now. Try again shortly."));
                var on = guestOptions.Value.Enabled;
                var captcha = CaptchaConfigured(http);
                GuestFeatureSettings Effective(GuestFeatureSettings f, bool configOn) => new(on && configOn && f.Enabled, captcha && f.CaptchaRequired);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new GuestStatusDto(
                    Effective(settings.Link, true),
                    Effective(settings.Upload, uploadOptions.Value.GuestsEnabled),
                    Effective(settings.Snippet, true)));
            })
            .WithTags("Guests")
            .AllowAnonymous()
            .RequireRateLimiting("guest-status");
    }

    private static string? Email(ClaimsPrincipal caller) => caller.FindFirstValue("email") ?? caller.FindFirstValue(ClaimTypes.Email);

    private static bool CaptchaConfigured(HttpContext http) => http.RequestServices.GetRequiredService<ICaptchaVerifier>() is not NoOpCaptchaVerifier;

    private static GuestControlsDto Describe(HttpContext http, GuestSettings settings, GuestSettingsStore store)
    {
        var services = http.RequestServices;
        return new GuestControlsDto(
            settings,
            store.Defaults,
            CaptchaConfigured(http),
            services.GetRequiredService<IOptions<GuestOptions>>().Value.Enabled,
            services.GetRequiredService<IOptions<UploadOptions>>().Value.GuestsEnabled,
            AccountCaptchaAlwaysOn: true,
            GuestControlService.Bounds);
    }
}
