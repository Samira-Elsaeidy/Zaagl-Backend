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

    /// <summary>Phase 2 (docs/phase2/README.md): create and manage tool links (vault, webhook catchers, capsules, comparisons,
    /// approvals, comment moderation). Owner reads use <see cref="ResourcesRead"/>.</summary>
    public const string ToolsWrite = "tools:write";

    public const string ProblemCode = "insufficient-scope";

    public static readonly IReadOnlyList<string> All =
        [LinksRead, LinksWrite, StatsRead, SnippetsWrite, FilesWrite, ResourcesRead, ToolsWrite];

    /// <summary>Owner PATCH/DELETE on <c>/api/resources/{id}</c>: any one of these passes the endpoint policy, then the handler checks the
    /// resource kind's own scope (<c>files:write</c> for files and media, plus <c>snippets:write</c> for snippets, <c>tools:write</c> or
    /// <c>files:write</c> for tool links).</summary>
    public static readonly IReadOnlyList<string> ResourceWriteScopes = [FilesWrite, ToolsWrite];

    public static string Policy(string scope) => "scope:" + scope;

    /// <summary>The policy name for "any one of <paramref name="scopes"/>": <c>scope:a|b</c>.</summary>
    public static string AnyPolicy(IReadOnlyList<string> scopes) => "scope:" + string.Join('|', scopes);

    /// <summary>True when the caller is a JWT session or an API key holding at least one of <paramref name="scopes"/>.</summary>
    public static bool AllowsAny(ClaimsPrincipal user, IReadOnlyList<string> scopes) => scopes.Any(s => Allows(user, s));

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
        builder.AddPolicy(AnyPolicy(ResourceWriteScopes), p => p
            .RequireAuthenticatedUser()
            .AddRequirements(new ScopeRequirement([.. ResourceWriteScopes])));
        return builder;
    }

    /// <summary>Requires <paramref name="scope"/> through the authorization middleware.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(Policy(scope));

    /// <summary>Requires any one of <paramref name="scopes"/> (a policy registered in <see cref="AddScopePolicies"/>).</summary>
    public static TBuilder RequireAnyScope<TBuilder>(this TBuilder builder, IReadOnlyList<string> scopes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(AnyPolicy(scopes));

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

public sealed class ScopeRequirement(params string[] scopes) : IAuthorizationRequirement
{
    /// <summary>Any one of these passes.</summary>
    public IReadOnlyList<string> Scopes { get; } = scopes;

    /// <summary>For the 403 message: <c>files:write</c>, or <c>files:write' or 'tools:write</c>.</summary>
    public string Scope => string.Join("' or '", Scopes);
}

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        if (ApiScopes.AllowsAny(context.User, requirement.Scopes))
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
