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
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Links;
using Zaagl.Modules.Resolution;

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
builder.Services.AddOpenApi();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Kestrel sits behind Nginx on the same host, which sets X-Forwarded-For from Cloudflare's real-IP header.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// HybridCache: L1 in-memory, L2 Valkey (Redis protocol).
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = valkey);
builder.Services.AddHybridCache();

builder.Services.AddHealthChecks()
    .AddNpgSql(postgres, name: "postgres")
    .AddRedis(valkey, name: "valkey");

builder.Services.AddCors(o => o.AddPolicy("web", policy => policy
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    static RateLimitPartition<string> Fixed(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
        });

    o.AddPolicy("auth", c => Fixed($"auth:{Ip(c)}", 20, TimeSpan.FromMinutes(1)));
    o.AddPolicy("link-write", c => Fixed($"link:{c.User.FindFirst("sub")?.Value ?? Ip(c)}", 60, TimeSpan.FromMinutes(1)));
    // Password guessing: tight limit per visitor and link.
    o.AddPolicy("unlock", c => Fixed($"unlock:{Ip(c)}:{c.Request.RouteValues["slug"]}", 10, TimeSpan.FromMinutes(1)));
});

// ---- Authentication -------------------------------------------------------

var authentication = builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = "Smart";
        o.DefaultChallengeScheme = "Smart";
    })
    .AddPolicyScheme("Smart", null, o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.ContainsKey(ApiKeyDefaults.Header)
            ? ApiKeyDefaults.Scheme
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
            // Browsers cannot set headers on WebSocket connections, so SignalR sends the token in the query string.
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
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
                var validator = ctx.HttpContext.RequestServices.GetRequiredService<Zaagl.Modules.Identity.Application.SecurityStampValidator>();
                if (!await validator.IsCurrentAsync(userId, stamp, ctx.HttpContext.RequestAborted))
                    ctx.Fail("This token has been superseded by a more recent sign-in or security event.");
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null)
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
    // Interactive sign-in only (no API keys): API key management and billing.
    .AddPolicy("Interactive", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser())
    // Spec §7.4: Admin role + TOTP (a normal session JWT only exists for an Admin after TOTP passed at
    // login - see IdentityService.LoginAsync) + the caller's IP inside Admin:AllowedNetworks. No admin
    // business endpoints exist yet (phase 5 builds them); this policy is ready for them.
    .AddPolicy("Admin", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole("Admin")
        .AddRequirements(new AdminNetworkRequirement()));

builder.Services.AddSignalR();

// ---- Modules --------------------------------------------------------------

builder.Services.AddHostedService<MigrationHostedService>(); // registered first so schemas exist before other hosted services run
builder.Services
    .AddIdentityModule(config)
    .AddBillingModule(config)
    .AddLinksModule(config)
    .AddAccessModule()
    .AddAnalyticsModule(config)
    .AddResolutionModule(config);
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
app.MapOrganizations();
app.MapLinks();
app.MapAccess();
app.MapAnalytics();
app.MapArchive();
app.MapBilling();
app.MapNotifications();
app.MapAdministration();
// Resolution owns the catch-all /{slug} route, so it is mapped last.
app.MapResolution();

app.Run();

public partial class Program;
