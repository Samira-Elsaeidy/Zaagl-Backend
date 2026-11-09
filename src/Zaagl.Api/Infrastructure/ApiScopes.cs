using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// Spec §7.4 (BE-001): API-key scopes. An interactive session (JWT) has every scope; an API key holds only
/// the scopes it was created with, carried as <c>scope</c> claims by <see cref="ApiKeyAuthenticationHandler"/>
/// together with <c>auth_method=api_key</c>. Every endpoint an API key can reach requires one scope policy.
/// </summary>
public static class ApiScopes
{
    public const string LinksRead = "links:read";
    public const string LinksWrite = "links:write";
    public const string StatsRead = "stats:read";

    // Phase 1 content scopes: accepted on key creation now, enforced once the content endpoints ship.
    public const string SnippetsWrite = "snippets:write";
    public const string FilesWrite = "files:write";
    public const string ResourcesRead = "resources:read";

    public const string ProblemCode = "insufficient-scope";

    public static readonly IReadOnlyList<string> All =
        [LinksRead, LinksWrite, StatsRead, SnippetsWrite, FilesWrite, ResourcesRead];

    public static string Policy(string scope) => "scope:" + scope;

    public static bool IsApiKey(ClaimsPrincipal user) => user.HasClaim("auth_method", "api_key");

    /// <summary>JWT sessions pass every scope; an API key must hold the claim.</summary>
    public static bool Allows(ClaimsPrincipal user, string scope) =>
        user.Identity?.IsAuthenticated == true && (!IsApiKey(user) || user.HasClaim("scope", scope));

    public static IResult InsufficientScope(string scope) =>
        ApiResults.Problem(new Error(ProblemCode, $"This API key lacks the '{scope}' scope."));

    /// <summary>Registers one policy per scope, the handler, and the 403 <c>insufficient-scope</c> problem body.</summary>
    public static AuthorizationBuilder AddScopePolicies(this AuthorizationBuilder builder)
    {
        builder.Services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ScopeAwareAuthorizationResultHandler>();
        foreach (var scope in All)
        {
            builder.AddPolicy(Policy(scope), p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new ScopeRequirement(scope)));
        }
        return builder;
    }

    /// <summary>Requires <paramref name="scope"/> through the authorization middleware.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(Policy(scope));

    /// <summary>
    /// For endpoints that also allow anonymous callers (guest link creation): <c>AllowAnonymous</c> skips
    /// authorization policies entirely, so the scope is checked in a filter for authenticated callers only.
    /// </summary>
    public static TBuilder RequireScopeWhenAuthenticated<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var user = context.HttpContext.User;
            if (user.Identity?.IsAuthenticated == true && !Allows(user, scope))
                return InsufficientScope(scope);
            return await next(context);
        });
}

public sealed class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        if (ApiScopes.Allows(context.User, requirement.Scope))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Turns a forbidden result caused by a missing scope into <c>403 insufficient-scope</c>; every other
/// result keeps the default behaviour (challenge / forbid through the authentication scheme).</summary>
public sealed class ScopeAwareAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<ScopeRequirement>().FirstOrDefault() is { } missing)
        {
            await ApiScopes.InsufficientScope(missing.Scope).ExecuteAsync(context);
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
