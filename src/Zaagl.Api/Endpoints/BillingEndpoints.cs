using System.Security.Claims;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Billing.Application;
using Zaagl.Modules.Identity.Application;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

public static class BillingEndpoints
{
    public static void MapBilling(this IEndpointRouteBuilder app)
    {
        var billing = app.MapGroup("/api/billing").WithTags("Billing");

        billing.MapGet("/me", async (ClaimsPrincipal user, BillingService service, CancellationToken ct) =>
            Results.Ok(await service.GetStatusAsync(user.GetUserId(), ct))).RequireAuthorization();

        billing.MapPost("/checkout", async (
                ClaimsPrincipal user, BillingService service, IdentityService identity, CancellationToken ct) =>
            {
                var me = await identity.GetUserAsync(user.GetUserId(), ct);
                return me is null
                    ? ApiResults.NotFound("User not found.")
                    : ApiResults.From(await service.CreateCheckoutAsync(me.Id, me.Email, ct));
            })
            .RequireAuthorization("Interactive");

        billing.MapPost("/portal", async (ClaimsPrincipal user, BillingService service, CancellationToken ct) =>
                ApiResults.From(await service.CreatePortalAsync(user.GetUserId(), ct)))
            .RequireAuthorization("Interactive");

        // Called by Stripe. Authenticity comes from the signature, not from a login.
        billing.MapPost("/webhook", async (HttpRequest request, BillingService service, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body);
                var payload = await reader.ReadToEndAsync(ct);
                var result = await service.HandleWebhookAsync(payload, request.Headers["Stripe-Signature"], ct);
                return result.IsSuccess ? Results.Ok() : ApiResults.Problem(result.Error!);
            })
            .AllowAnonymous();
    }
}
