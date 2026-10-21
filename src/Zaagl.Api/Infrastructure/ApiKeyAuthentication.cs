using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Identity.Application;

namespace Zaagl.Api.Infrastructure;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";
}

/// <summary>Authenticates programmatic callers that send an <c>X-Api-Key</c> header.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.Header, out var header) || header.Count != 1)
            return AuthenticateResult.NoResult();

        var identity = Context.RequestServices.GetRequiredService<IdentityService>();
        var userId = await identity.ValidateApiKeyAsync(header.ToString(), Context.RequestAborted);
        if (userId is null) return AuthenticateResult.Fail("Invalid API key.");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", userId.Value.ToString()), new Claim("auth_method", "api_key")],
            Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
