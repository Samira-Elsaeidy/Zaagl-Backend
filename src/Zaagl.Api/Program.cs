using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters. Set it via configuration or an environment variable.");

// ---- Infrastructure -------------------------------------------------------

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
            IssuerSigningKey = jwt.SigningKey(),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // Browsers cannot set headers on WebSocket connections, so SignalR sends the token in the query string.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
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

builder.Services.AddAuthorizationBuilder()
    // Interactive sign-in only (no API keys): API key management and billing.
    .AddPolicy("Interactive", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());

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

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();

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

app.MapHealthChecks("/health");
app.MapHub<AnalyticsHub>("/hubs/analytics");

app.MapIdentity();
app.MapOrganizations();
app.MapLinks();
app.MapAccess();
app.MapAnalytics();
app.MapBilling();
app.MapNotifications();
app.MapAdministration();
// Resolution owns the catch-all /{slug} route, so it is mapped last.
app.MapResolution();

app.Run();

public partial class Program;
