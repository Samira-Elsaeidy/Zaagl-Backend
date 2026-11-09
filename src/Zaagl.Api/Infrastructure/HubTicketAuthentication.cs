using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Domain;

namespace Zaagl.Api.Infrastructure;

public static class HubTicketDefaults
{
    public const string Scheme = "HubTicket";
    public const string QueryParameter = "ticket";
    public const string HubPrefix = "/hubs";

    /// <summary>True for a /hubs/* request that carries <c>?ticket=</c>; the "Smart" scheme forwards it here.</summary>
    public static bool Applies(HttpRequest request) =>
        request.Path.StartsWithSegments(HubPrefix) && request.Query.ContainsKey(QueryParameter);
}

/// <summary>
/// PD-41: authenticates a SignalR connection by a single-use hub ticket from POST /api/auth/hub-ticket instead of
/// an access token in the query string. The negotiate request only checks the ticket; the connect request (the
/// WebSocket upgrade) consumes it, so a ticket opens exactly one connection. Transports that send several
/// HTTP requests per connection (long polling, server-sent events) therefore fail after the first; clients use
/// WebSockets. The principal carries <c>sub</c> and <c>auth_method=hub_ticket</c>, never an API-key scope set.
/// </summary>
public sealed class HubTicketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HubTicketDefaults.Applies(Request)) return AuthenticateResult.NoResult();

        var ticket = Request.Query[HubTicketDefaults.QueryParameter].ToString();
        var codes = Context.RequestServices.GetRequiredService<OneTimeCodeService>();
        var negotiate = Request.Path.Value?.EndsWith("/negotiate", StringComparison.Ordinal) == true;
        var userId = negotiate
            ? await codes.PeekAsync(ticket, OneTimeCodePurpose.HubTicket, Context.RequestAborted)
            : await codes.ConsumeAsync(ticket, OneTimeCodePurpose.HubTicket, Context.RequestAborted);
        if (userId is null) return AuthenticateResult.Fail("Invalid, expired or already used hub ticket.");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", userId.Value.ToString()), new Claim("auth_method", "hub_ticket")], Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
