using System.Security.Claims;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.DeveloperTools.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Net;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// Phase 2 (docs/phase2/README.md): who creates a tool link. An account is its user id. A request without any credential is a
/// guest only on routes that allow guests; a failing credential never reaches this (PD-36, <c>RejectFailedCredentials</c>).
/// Guests go through exactly the Phase 1 guest gates, in the same order as snippets: <c>Guests:Enabled</c>, the Turnstile CAPTCHA
/// for <paramref name="captchaAction"/> (spec §7.12: hostname, action, age), the in-memory brakes (global ceiling, per minute) and
/// the PD-14 counters (30 per hour, 60 per 24 h per IP partition, shared with snippets and uploads), which the service consumes
/// after its plan checks and refunds when the creation fails.
/// </summary>
public static class ToolActors
{
    public static async Task<(ToolActor? Actor, IResult? Refusal)> ResolveAsync(
        HttpContext http, ClaimsPrincipal user, string captchaAction, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated == true)
            return (new ToolActor(user.GetUserId(), null, SnippetsEndpoints.CreatedViaFor(user)), null);

        var services = http.RequestServices;
        var opts = services.GetRequiredService<IOptions<GuestOptions>>().Value;
        if (!opts.Enabled) return (null, ApiResults.Problem(Error.ServiceUnavailable("Guest creation is currently disabled.")));

        var captcha = await CaptchaGate.CheckAsync(http, captchaAction, ct);
        if (captcha is not null) return (null, captcha);

        var partition = IpPartition.Key(http.Connection.RemoteIpAddress);
        var limiter = services.GetRequiredService<GuestRateLimiter>();
        if (!limiter.TryGlobalCeiling(partition, opts.GlobalPerHour))
            return (null, ApiResults.Problem(new Error(ErrorCodes.GuestCreationPaused, "Guest creation is temporarily paused for this network. Try again shortly.")));
        if (!limiter.TryPerMinute(partition, opts.PerMinute))
            return (null, ApiResults.Problem(Error.TooManyRequests("Too many requests. Try again in a minute.")));

        var guestKey = services.GetRequiredService<GuestKeyProvider>();
        var linksDb = services.GetRequiredService<LinksDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var counterKey = GuestContentQuota.PrefixKey(guestKey.Key, partition);
        DateTime consumedAt = default;
        var admitted = false;
        return (new ToolActor(null, GuestIdentity.DeriveKey(guestKey.Key, partition), Modules.Resources.Contracts.CreatedVia.Web,
            async token =>
            {
                consumedAt = clock.GetUtcNow().UtcDateTime;
                admitted = await GuestContentQuota.TryConsumeAsync(linksDb, counterKey, opts.ContentPerHour, opts.ContentPerDay, consumedAt, token);
                return admitted;
            },
            async () =>
            {
                if (!admitted) return;
                admitted = false;
                await GuestContentQuota.RefundAsync(linksDb, counterKey, consumedAt);
            }), null);
    }
}
