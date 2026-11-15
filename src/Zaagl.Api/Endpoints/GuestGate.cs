using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// The guest-creation control panel at request time, shared by the three guest create routes (POST /api/links,
/// POST /api/snippets, the two upload-session creates). In order:
///  1. configuration kill switch <c>Guests:Enabled</c> (spec §7.12) - off: 503 <c>guest_creation_paused</c>;
///  2. the run-time settings (<see cref="GuestSettingsStore"/>) - unreadable with nothing known: 503 <c>service_unavailable</c>,
///     never a silent fall-back to "open";
///  3. the feature paused by an admin: 503 <c>guest_creation_paused</c>;
///  4. CAPTCHA, only when the feature requires it (spec §7.12 verifier: 400 <c>captcha-required</c>/<c>captcha-invalid</c>,
///     503 when Turnstile is down).
/// Refusals of 3 and 4 are counted for the panel's stats (in memory, nothing about the visitor).
/// Account routes (register, forgot-password, resend-confirmation) never come here: their CAPTCHA (<see cref="CaptchaGate"/>)
/// cannot be switched off from the panel.
/// </summary>
public static class GuestGate
{
    public static async Task<(GuestSettings? Settings, IResult? Refusal)> AdmitAsync(HttpContext http, string feature, CancellationToken ct)
    {
        var services = http.RequestServices;
        if (!services.GetRequiredService<IOptions<GuestOptions>>().Value.Enabled)
            return (null, ApiResults.Problem(Error.Of(ErrorCodes.GuestCreationPaused, "Guest creation is turned off. Sign in to continue.")));

        var settings = await services.GetRequiredService<GuestSettingsStore>().GetAsync(ct);
        if (settings is null)
            return (null, ApiResults.Problem(Error.ServiceUnavailable("Guest creation is unavailable right now. Try again shortly.")));

        var counters = services.GetRequiredService<GuestControlCounters>();
        var state = settings.For(feature);
        if (!state.Enabled)
        {
            counters.PausedRefusal(feature);
            return (settings, ApiResults.Problem(Error.Of(ErrorCodes.GuestCreationPaused, PausedMessage(feature))));
        }

        if (state.CaptchaRequired)
        {
            var refusal = await CaptchaGate.CheckAsync(http, feature, ct);
            if (refusal is not null)
            {
                counters.CaptchaFailed(feature);
                return (settings, refusal);
            }
        }
        return (settings, null);
    }

    public static string PausedMessage(string feature) => feature switch
    {
        GuestFeatures.Link => "Guest link creation is paused right now. Sign in, or try again later.",
        GuestFeatures.Upload => "Guest uploads are paused right now. Sign in, or try again later.",
        GuestFeatures.Drop => "Uploads to drop-off portals are paused right now. Try again later.",
        GuestFeatures.Table => "Guest table links are paused right now. Sign in, or try again later.",
        _ => "Guest snippet creation is paused right now. Sign in, or try again later.",
    };
}
