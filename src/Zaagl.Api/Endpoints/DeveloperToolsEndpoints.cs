using System.Security.Claims;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Access.Contracts;
using Zaagl.Modules.DeveloperTools.Application;
using Zaagl.Modules.DeveloperTools.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;
using LinkOptions = Zaagl.Modules.Links.Application.LinkOptions;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi SecretCreateRequest (docs/phase2/vault.md).</summary>
public sealed record SecretCreateRequest(
    string? Ciphertext,
    bool PassphraseProtected = false,
    int? MaxViews = null,
    DateTime? ExpiresAt = null,
    string? Label = null,
    string? Password = null,
    string? CustomSlug = null);

/// <summary>
/// Phase 2 E2.1 developer tools on <c>api.zaa.gl</c> (docs/phase2). Owner routes are owner only (404 otherwise, PD-40) and
/// <c>private, no-store</c>; public routes run <see cref="IAccessGate"/> on every call and only a POST consumes anything.
/// </summary>
public static class DeveloperToolsEndpoints
{
    /// <summary>Spec §7.12 action name for guest vault creation (shared with the website's CAPTCHA widget).</summary>
    public const string GuestVaultAction = "guest-vault";

    /// <summary>The largest vault envelope (1 MiB) as base64url plus JSON overhead.</summary>
    private const long VaultBodyBytes = 2 * 1024 * 1024;

    public static void MapDeveloperTools(this IEndpointRouteBuilder app)
    {
        MapVault(app);
    }

    private static void MapVault(IEndpointRouteBuilder app)
    {
        var vault = app.MapGroup("/api/vault/secrets").WithTags("Vault");

        vault.MapPost("", async (SecretCreateRequest request, ClaimsPrincipal user, HttpContext http, VaultService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var (actor, refusal) = await ToolActors.ResolveAsync(http, user, GuestVaultAction, ct);
                if (refusal is not null) return refusal;
                var created = await service.CreateAsync(actor!, new CreateSecretCommand(
                    request.Ciphertext, request.PassphraseProtected, request.MaxViews, request.ExpiresAt, request.Label,
                    request.Password, request.CustomSlug), ct);
                return ApiResults.From(created, dto => Results.Created($"/api/vault/secrets/{dto.ResourceId}", dto));
            })
            .AllowAnonymous()
            .RejectFailedCredentials() // PD-36: a failing credential is a 401, never a guest secret
            .RequireScopeWhenAuthenticated(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithRequestBodyLimit(VaultBodyBytes)
            .WithSummary("Create an end-to-end encrypted secret (ciphertext only; the key stays in the URL fragment).")
            .Produces<SecretCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status429TooManyRequests);

        vault.MapGet("", async (ClaimsPrincipal user, HttpContext http, VaultService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.Ok(await service.ListAsync(user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("The caller's secrets, newest first (at most 100). Metadata only, never ciphertext.")
            .Produces<IReadOnlyList<OwnerSecretDto>>();

        vault.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, VaultService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var owned = await service.ResolveOwnedAsync(id, user.GetUserId(), ct);
                if (!owned.IsSuccess) return ApiResults.Problem(owned.Error!);
                var o = owned.Value!;
                var events = await service.EventsAsync(id, ct);
                return Results.Ok(VaultService.ToOwnerDto(o.Resource, o.Secret, o.Link!, o.State, links.Value.BaseUrl, events));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("One of the caller's secrets with its audit trail (no contents, no IP).")
            .Produces<OwnerSecretDto>().ProducesProblem(StatusCodes.Status404NotFound);

        vault.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, VaultService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var destroyed = await service.DestroyAsync(id, user.GetUserId(), ct);
                return destroyed.IsSuccess ? Results.NoContent() : ApiResults.Problem(destroyed.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Destroy a secret now: the link answers 410 and the ciphertext is wiped.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var open = app.MapGroup("/api/public/vault").WithTags("Vault (public)").AllowAnonymous();

        open.MapGet("/{code}", async (string code, HttpContext http, IAccessGate gate, ILinkResolver links, VaultService service, CancellationToken ct) =>
            {
                NoStore(http);
                var gated = await GateAsync(http, gate, code, LinkTargetKind.Secret, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);
                if (link is null) return ApiResults.NotFound();
                var described = await service.DescribeAsync(gated.ResourceId, gated.LinkState, link.ExpiresAt ?? default, link.HasPassword, ct);
                return ApiResults.From(described);
            })
            .RequireRateLimiting("content-read")
            .WithSummary("Vault descriptor for a link holder. Consumes nothing (safe for previews and reloads).")
            .Produces<SecretDescriptorDto>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);

        open.MapPost("/{code}/reveal", async (string code, HttpContext http, IAccessGate gate, VaultService service, CancellationToken ct) =>
            {
                NoStore(http);
                var gated = await GateAsync(http, gate, code, LinkTargetKind.Secret, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                return ApiResults.From(await service.RevealAsync(gated.ResourceId, gated.LinkState, ct));
            })
            .RequireRateLimiting("content-read")
            .WithSummary("Consume one view and return the ciphertext envelope; the last view destroys it.")
            .Produces<SecretRevealDto>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);
    }

    internal readonly record struct Gated(IResult? Refusal, Guid ResourceId, LinkState? LinkState);

    /// <summary>The Phase 1 gate (state, link key, password grant), then the kind check: a code of another kind answers exactly
    /// like an unknown code.</summary>
    internal static async Task<Gated> GateAsync(HttpContext http, IAccessGate gate, string code, LinkTargetKind kind, CancellationToken ct,
        AccessPurpose purpose = AccessPurpose.View)
    {
        var credentials = new AccessCredentials(Header(http, AccessWireNames.LinkKeyHeader), Header(http, AccessWireNames.GrantHeader));
        var gated = await gate.EvaluateAsync(code, credentials, purpose, ct);
        if (gated.Outcome != AccessGateOutcome.Allowed) return new(ApiResults.Problem(ContentAccessGate.ToError(gated)), default, null);
        if (gated.Kind != kind || gated.ResourceId is not { } resourceId) return new(ApiResults.NotFound(), default, null);
        return new(null, resourceId, gated.LinkState);
    }

    internal static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers["X-Robots-Tag"] = "noindex";
    }

    internal static string? Header(HttpContext http, string name)
    {
        var value = http.Request.Headers[name].ToString();
        return value.Length == 0 ? null : value;
    }
}
