using System.Security.Claims;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Net;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// Who is uploading (ARC-009 section 5 step 1 and 2). An account is its user id. With no credential, guests (when both
/// <c>Guests:Enabled</c> and <c>Uploads:GuestsEnabled</c> are on) are the same derived guest key snippets use, so a session belongs to its
/// creator's network partition and nobody else can reach it. Only the two creating routes (tus create, <c>/uploads/simple</c>) check
/// the CAPTCHA (action <c>guest-upload</c>), the in-memory brakes of spec 7.12 and the PD-14 counters (30 per hour, 60 per 24 h, shared with
/// snippets, answer <c>429 quota</c>); the counters are consumed by the service after the plan checks and refunded when the create fails.
/// A failing credential never gets here (it is a 401 first, PD-36).
/// </summary>
public static class UploadActors
{
    public static async Task<(UploadActor Actor, IResult? Refusal)> ResolveAsync(
        HttpContext http, ClaimsPrincipal user, bool creating, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated == true)
            return (new UploadActor(user.GetUserId(), null, SnippetsEndpoints.CreatedViaFor(user)), null);

        var services = http.RequestServices;
        var guestOptions = services.GetRequiredService<IOptions<GuestOptions>>().Value;
        var uploadOptions = services.GetRequiredService<IOptions<UploadOptions>>().Value;
        var none = new UploadActor(null, null, CreatedVia.Web);
        if (!guestOptions.Enabled || !uploadOptions.GuestsEnabled) return (none, null); // the service answers 503 guest_creation_paused

        var guestKey = services.GetRequiredService<GuestKeyProvider>();
        var partition = IpPartition.Key(http.Connection.RemoteIpAddress);
        var creatorKey = GuestIdentity.DeriveKey(guestKey.Key, partition);
        if (!creating) return (new UploadActor(null, creatorKey, CreatedVia.Web), null);

        var config = services.GetRequiredService<IConfiguration>();
        var captchaToken = http.Request.Headers["Captcha-Token"].ToString();
        var frontendHost = Uri.TryCreate(config["Frontend:BaseUrl"], UriKind.Absolute, out var frontendUri) ? frontendUri.Host : "";
        var verified = await services.GetRequiredService<ICaptchaVerifier>()
            .VerifyAsync(string.IsNullOrEmpty(captchaToken) ? null : captchaToken, "guest-upload", frontendHost, ct);
        if (!verified.IsSuccess) return (none, ApiResults.Problem(verified.Error!));

        var limiter = services.GetRequiredService<GuestRateLimiter>();
        if (!limiter.TryGlobalCeiling(partition, guestOptions.GlobalPerHour))
            return (none, ApiResults.Problem(new Error(ErrorCodes.GuestCreationPaused, "Guest creation is temporarily paused for this network. Try again shortly.")));
        if (!limiter.TryPerMinute(partition, guestOptions.PerMinute))
            return (none, ApiResults.Problem(Error.TooManyRequests("Too many requests. Try again in a minute.")));

        var linksDb = services.GetRequiredService<LinksDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var counterKey = GuestContentQuota.PrefixKey(guestKey.Key, partition);
        DateTime consumedAt = default;
        return (new UploadActor(null, creatorKey, CreatedVia.Web,
            async token =>
            {
                consumedAt = clock.GetUtcNow().UtcDateTime;
                return await GuestContentQuota.TryConsumeAsync(linksDb, counterKey, guestOptions.ContentPerHour, guestOptions.ContentPerDay, consumedAt, token);
            },
            () => GuestContentQuota.RefundAsync(linksDb, counterKey, consumedAt)), null);
    }
}
