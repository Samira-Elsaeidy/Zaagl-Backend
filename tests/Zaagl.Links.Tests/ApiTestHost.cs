using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zaagl.Links.Tests;

/// <summary>
/// A minimal in-memory host for endpoint-level tests that must not touch the database: only the endpoint
/// groups under test are mapped, every service they bind is a placeholder that fails when used, and an
/// unhandled exception becomes status <see cref="ReachedHandler"/> - proof that the request passed
/// authorization, routing and rate limiting and got into the handler.
/// </summary>
internal static class ApiTestHost
{
    public const int ReachedHandler = 599;
    public const string PrincipalHeader = "X-Test-Principal";

    public static async Task<WebApplication> StartAsync(
        Action<IServiceCollection> configureServices, Action<WebApplication> map, Action<WebApplication>? beforeRouting = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        configureServices(builder.Services);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception) when (!context.Response.HasStarted) { context.Response.StatusCode = ReachedHandler; }
        });
        beforeRouting?.Invoke(app);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        map(app);
        await app.StartAsync();
        return app;
    }

    /// <summary>Registers a placeholder for <paramref name="type"/>: interfaces get a proxy that throws on any
    /// call, classes an uninitialised instance (any member that touches state throws).</summary>
    public static void AddPlaceholder(this IServiceCollection services, Type type) =>
        services.AddSingleton(type, _ => type.IsInterface
            ? DispatchProxy.Create(type, typeof(ThrowingProxy))
            : RuntimeHelpers.GetUninitializedObject(type));

    public class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Placeholder service called: {targetMethod?.Name}");
    }

    /// <summary><c>X-Test-Principal: jwt</c> is a session; <c>apikey:links:read stats:read</c> is an API key
    /// with those scopes (claims shaped exactly like ApiKeyAuthenticationHandler's). No header: anonymous.</summary>
    public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers[PrincipalHeader].ToString();
            if (string.IsNullOrEmpty(header)) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()) };
            if (header.StartsWith("apikey", StringComparison.Ordinal))
            {
                claims.Add(new("auth_method", "api_key"));
                claims.AddRange(header["apikey".Length..].TrimStart(':')
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => new Claim("scope", s)));
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
