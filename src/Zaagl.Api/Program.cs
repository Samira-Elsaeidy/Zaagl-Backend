using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Zaagl.Api.Endpoints;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access;
using Zaagl.Modules.Analytics;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Billing;
using Zaagl.Modules.Files;
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Links;
using Zaagl.Modules.Resolution;
using Zaagl.Modules.Resources;
using Zaagl.Modules.Snippets;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var postgres = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var valkey = config.GetConnectionString("Valkey")
    ?? throw new InvalidOperationException("ConnectionStrings:Valkey is not configured.");

var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.EffectiveKeys().Any(k => k.Key.Length < 32))
    throw new InvalidOperationException("Every Jwt signing key must be at least 32 characters. Set Jwt:Key (or Jwt:Keys) via configuration or an environment variable.");

// ---- Infrastructure -------------------------------------------------------

builder.Services.AddSingleton<Zaagl.SharedKernel.Testing.IFailpoints, Zaagl.SharedKernel.Testing.NoOpFailpoints>();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<HostRoutingOptions>(config.GetSection(HostRoutingOptions.Section));

// BE-005: small global request-body limit; endpoints that need more raise it per endpoint (WithRequestBodyLimit).
var bodyLimits = config.GetSection(RequestBodyLimitOptions.Section).Get<RequestBodyLimitOptions>() ?? new RequestBodyLimitOptions();
builder.WebHost.ConfigureKestrel(o => bodyLimits.Apply(o.Limits));
builder.Services.AddOpenApi();
// BE-002: every problem body (ours and the framework's) carries type https://zaa.gl/problems/<code>, title, status, code.
builder.Services.AddExceptionHandler<Zaagl.Api.Infrastructure.ApiExceptionHandler>();
// Binding failures throw BadHttpRequestException in every environment (the default is Development only), so ApiExceptionHandler
// turns all of them into the same 400 invalid_request problem+json instead of an empty framework 400 in production.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx => ApiResults.Normalize(ctx.ProblemDetails));

// Spec §7.13 (BE-030): forwarded headers are trusted only from Network:KnownProxies (exact addresses of the
// reverse proxy and the web/BFF container) and Network:KnownNetworks (staging gate ranges). Nothing configured
// means nothing is trusted. Parsed once here so a bad entry fails startup.
var network = config.GetSection(NetworkOptions.Section).Get<NetworkOptions>() ?? new NetworkOptions();
builder.Services.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Apply(o, network));

// HybridCache: L1 in-memory, L2 Valkey (Redis protocol). L2 is optional at run time: every L2 operation is
// time-boxed and circuit-broken (Cache:L2), so a Valkey outage never fails a write or slows a redirect
// beyond one short timeout; reads fall back to the database (spec §7.0, §7.3).
builder.Services.Configure<L2CacheOptions>(config.GetSection(L2CacheOptions.Section));
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = valkey);
ResilientDistributedCache.Decorate(builder.Services);
builder.Services.AddHybridCache();

builder.Services.AddHealthChecks()
    .AddNpgSql(postgres, name: "postgres")
    .AddRedis(valkey, name: "valkey");

builder.Services.AddCors(o => o.AddPolicy("web", policy => policy
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// Spec §7.3: redirect limits (300/min, tiered misses), in memory per instance behind IRedirectRateLimiter.
builder.Services.Configure<RedirectRateLimitOptions>(config.GetSection(RedirectRateLimitOptions.Section));
builder.Services.AddSingleton<IRedirectRateLimiter, InMemoryRedirectRateLimiter>();

var rateLimits = config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Spec §7.3: every IP-based limiter partitions by IpPartition (IPv4 address, IPv6 /64).
    static string Ip(HttpContext c) => Zaagl.SharedKernel.Net.IpPartition.Key(c.Connection.RemoteIpAddress);
    static RateLimitPartition<string> Fixed(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
        });

    o.AddPolicy("auth", c => Fixed($"auth:{Ip(c)}", rateLimits.AuthPerMinute, TimeSpan.FromMinutes(1)));
    // BE-024 / ARC-014 §2: device flow. Activation is partitioned by the signed-in user (the wrong-code lockout is in DeviceFlowService).
    o.AddPolicy("device-code", c => Fixed($"dcode:{Ip(c)}", rateLimits.DeviceCodePerMinute, TimeSpan.FromMinutes(1)));
    o.AddPolicy("device-token", c => Fixed($"dtoken:{Ip(c)}", rateLimits.DeviceTokenPerMinute, TimeSpan.FromMinutes(1)));
    o.AddPolicy("device-activate", c => Fixed($"dact:{c.User.FindFirst("sub")?.Value ?? Ip(c)}", rateLimits.DeviceActivatePerMinute, TimeSpan.FromMinutes(1)));
    o.AddPolicy("link-write", c => Fixed($"link:{c.User.FindFirst("sub")?.Value ?? Ip(c)}", rateLimits.LinkWritePerMinute, TimeSpan.FromMinutes(1)));
    // Password guessing: tight limit per visitor and link.
    o.AddPolicy("unlock", c => Fixed($"unlock:{Ip(c)}:{c.Request.RouteValues["slug"] ?? c.Request.RouteValues["code"]}", rateLimits.UnlockPerMinute, TimeSpan.FromMinutes(1)));
    // ARC-003 §4.1 (ARC-013 values OPEN, technical defaults): the content API partitions by IP and code.
    o.AddPolicy("content-read", c => Fixed($"cread:{Ip(c)}:{c.Request.RouteValues["code"]}", rateLimits.ContentReadPerMinute, TimeSpan.FromMinutes(1)));
    o.AddPolicy("content-write", c => Fixed($"cwrite:{c.User.FindFirst("sub")?.Value ?? Ip(c)}", rateLimits.ContentWritePerMinute, TimeSpan.FromMinutes(1)));
});

// ---- Authentication -------------------------------------------------------

var authentication = builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = "Smart";
        o.DefaultChallengeScheme = "Smart";
    })
    .AddPolicyScheme("Smart", null, o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.ContainsKey(ApiKeyDefaults.Header) ? ApiKeyDefaults.Scheme
        : HubTicketDefaults.Applies(ctx.Request) ? HubTicketDefaults.Scheme // PD-41: /hubs/*?ticket=
        : JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            // Spec §7.4: multiple keys validate (a {kid, key} set), only the newest signs - the
            // mechanism a future key rotation needs, even with just one entry configured today.
            IssuerSigningKeys = jwt.AllValidationKeys(),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        o.Events = new JwtBearerEvents
        {
            // PD-41: no access token is ever read from the query string. Browsers open /hubs/* with a single-use
            // ?ticket= (POST /api/auth/hub-ticket, HubTicketAuthenticationHandler); "?access_token=" is ignored.
            // Spec §7.4: the "sstamp" claim must match the user's current security stamp, so a password
            // reset or logout-everywhere invalidates every outstanding access token within ~60s (cached),
            // not just at its natural 15-minute expiry.
            OnTokenValidated = async ctx =>
            {
                var sub = ctx.Principal?.FindFirstValue("sub");
                var stamp = ctx.Principal?.FindFirstValue("sstamp");
                if (sub is null || stamp is null || !Guid.TryParse(sub, out var userId))
                {
                    ctx.Fail("Token is missing required claims.");
                    return;
                }
                // Spec §7.4 (AZ-01): the Admin enrol-only token is valid only for /totp/enroll and /totp/confirm.
                if (CredentialRules.EnrolOnlyTokenMisused(ctx.HttpContext, ctx.Principal))
                {
                    ctx.Fail("The enrol-only token is valid only for TOTP enrolment.");
                    return;
                }
                var validator = ctx.HttpContext.RequestServices.GetRequiredService<Zaagl.Modules.Identity.Application.SecurityStampValidator>();
                if (!await validator.IsCurrentAsync(userId, stamp, ctx.HttpContext.RequestAborted))
                {
                    ctx.Fail("This token has been superseded by a more recent sign-in or security event.");
                    return;
                }
                // ARC-014 §2.4: a device token whose session was revoked stops working (60 s cache bound).
                if (Guid.TryParse(ctx.Principal?.FindFirstValue(Zaagl.Modules.Identity.Contracts.NativeClientClaims.SessionId), out var sid)
                    && !await ctx.HttpContext.RequestServices.GetRequiredService<Zaagl.Modules.Identity.Application.DeviceSessionValidator>()
                        .IsActiveAsync(sid, ctx.HttpContext.RequestAborted))
                    ctx.Fail("This device session has been revoked.");
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null)
    .AddScheme<AuthenticationSchemeOptions, HubTicketAuthenticationHandler>(HubTicketDefaults.Scheme, null)
    .AddCookie(IdentityEndpoints.ExternalScheme, o =>
    {
        o.Cookie.Name = ".zaagl.external";
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.HttpOnly = true;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });

// Social providers are only registered when credentials are configured.
if (!string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"]))
{
    authentication.AddGoogle("Google", o =>
    {
        o.ClientId = config["Authentication:Google:ClientId"]!;
        o.ClientSecret = config["Authentication:Google:ClientSecret"] ?? "";
        o.SignInScheme = IdentityEndpoints.ExternalScheme;
        o.CallbackPath = "/api/auth/signin-google";
    });
}
if (!string.IsNullOrWhiteSpace(config["Authentication:GitHub:ClientId"]))
{
    authentication.AddGitHub("GitHub", o =>
    {
        o.ClientId = config["Authentication:GitHub:ClientId"]!;
        o.ClientSecret = config["Authentication:GitHub:ClientSecret"] ?? "";
        o.Scope.Add("user:email");
        o.SignInScheme = IdentityEndpoints.ExternalScheme;
        o.CallbackPath = "/api/auth/signin-github";
    });
}

builder.Services.Configure<AdminNetworkOptions>(config.GetSection(AdminNetworkOptions.Section));
builder.Services.AddSingleton<IAuthorizationHandler, AdminNetworkHandler>();

builder.Services.AddAuthorizationBuilder()
    // Interactive sign-in only (no API keys): API key management, account (/auth/me, /me, logout-everywhere)
    // and billing. The enrol-only token is refused here too, except where AllowEnrolOnlyToken() marks the endpoint.
    // ARC-014 §3.1 / C11: a native (CLI/desktop, amr=device) token is never Interactive -> 403.
    .AddPolicy("Interactive", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => !NativeClientPolicy.IsNativeToken(ctx.User)))
    // ARC-014 §3.1: any valid JWT, session or native (device) token, but never an API key. Used by /api/auth/whoami only.
    .AddPolicy("AnyJwt", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser())
    // Spec §7.4: Admin role + TOTP (a normal session JWT only exists for an Admin after TOTP passed at
    // login - see IdentityService.LoginAsync) + the caller's IP inside Admin:AllowedNetworks. No admin
    // business endpoints exist yet (phase 5 builds them); this policy is ready for them.
    // ARC-014 §2.4 / MUST NOT 17: native tokens never carry Admin and are refused here as well.
    .AddPolicy("Admin", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole("Admin")
        .RequireAssertion(ctx => !NativeClientPolicy.IsNativeToken(ctx.User))
        .AddRequirements(new AdminNetworkRequirement()))
    // Spec §7.4 (BE-001): "scope:<scope>" policies; a session JWT passes, an API key must hold the scope.
    .AddScopePolicies();

builder.Services.AddSignalR();

// ---- Modules --------------------------------------------------------------

builder.Services.AddHostedService<MigrationHostedService>(); // registered first so schemas exist before other hosted services run
builder.Services
    .AddIdentityModule(config)
    .AddBillingModule(config)
    .AddLinksModule(config)
    .AddAccessModule(config, builder.Environment.IsProduction())
    .AddAnalyticsModule(config)
    .AddResolutionModule(config)
    .AddFilesModule(config)
    .AddFilesUploads() // BE-011/BE-012: tus sessions, /complete (the scan task itself runs in the worker)
    .AddResourcesModule(config, builder.Environment.IsProduction()) // BE-006/BE-008: resources schema, registry, quota, raw/owner tickets
    .AddSnippetsModule(config); // BE-008: snippets schema, immutable versions, create/edit/read
builder.Services.AddScoped<ObjectDelivery>(); // ARC-008 hop for stored objects
builder.Services.AddSingleton<ILiveClickPublisher, SignalRLiveClickPublisher>();

// ---- Pipeline -------------------------------------------------------------

var app = builder.Build();

// Spec §3 "migrator": `docker compose run --rm api migrate` (or `dotnet run -- migrate`) applies every
// module's EF Core migrations, logs the same "Migrated {Module}" lines MigrationHostedService would, and
// exits 0 - no web host, no hosted services. Deliberate scoped shortcut vs. the literal spec: this reuses
// the Zaagl.Api image/DI graph directly rather than a separate Zaagl.Ops project (not built in this build);
// it does not yet retry with the owner-role `lock_timeout = 5s` behaviour the spec describes for `migrator`.
if (args.Contains("migrate"))
{
    var migrateLogger = app.Services.GetRequiredService<ILogger<Program>>();
    foreach (var migrator in app.Services.GetServices<Zaagl.SharedKernel.IModuleMigrator>())
    {
        await migrator.MigrateAsync(CancellationToken.None);
        migrateLogger.LogInformation("Migrated {Module}", migrator.Module);
    }
    return;
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
// Any exception that escapes an endpoint becomes a clean problem+json (ApiExceptionHandler), in every environment.
app.UseExceptionHandler();
app.UseHostRouting();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors("web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Spec MUST-NOT #33: a non-no-op IFailpoints implementation must never run outside the Testing environment.
if (app.Services.GetRequiredService<Zaagl.SharedKernel.Testing.IFailpoints>() is not Zaagl.SharedKernel.Testing.NoOpFailpoints
    && app.Environment.EnvironmentName != "Testing")
{
    throw new InvalidOperationException("A non-no-op IFailpoints implementation is registered outside the Testing environment.");
}

app.MapHealthChecks("/health");
// Spec §7.7: /health/live is "process up" only (no dependency checks, so a database blip never takes
// this replica out of the proxy); /health/ready checks both data sources and MUST NOT depend on the
// coordinator, a lease or the key-fingerprint check (MUST-NOT #21) - it only runs the two checks
// registered above (postgres, valkey), neither of which touches the coordinator.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/health/issuer", (Zaagl.SharedKernel.Coordination.CreationGate gate) =>
    Results.Ok(new { state = gate.State.ToString() }));
app.MapHub<AnalyticsHub>("/hubs/analytics");

app.MapIdentity();
app.MapDeviceFlow();
app.MapOrganizations();
app.MapLinks();
app.MapAccess();
app.MapAnalytics();
app.MapArchive();
app.MapBilling();
app.MapNotifications();
app.MapAdministration();
app.MapSnippets();
app.MapFiles();     // BE-011/BE-012: tus uploads and /complete
app.MapResources(); // F-12: owner library, status, patch, delete, owner tickets, /api/me/storage
app.MapPublicContent();
app.MapDelivery();
// Resolution owns the catch-all /{slug} route, so it is mapped last.
app.MapResolution();

app.Run();

public partial class Program;
