using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.Persistence;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Modules.Links;

public static class LinksModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", LinksDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddLinksModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<LinkOptions>(configuration.GetSection(LinkOptions.Section));
        services.Configure<WebRiskOptions>(configuration.GetSection(WebRiskOptions.Section));
        services.Configure<GuestOptions>(configuration.GetSection(GuestOptions.Section));
        services.Configure<SafetyOptions>(configuration.GetSection(SafetyOptions.Section));
        services.Configure<AbuseOptions>(configuration.GetSection(AbuseOptions.Section));
        services.Configure<CaptchaOptions>(configuration.GetSection(CaptchaOptions.Section));

        // Spec §7.12: changing GUEST_KEY only resets guest quotas, it never affects already-issued links -
        // same dev-only-fallback shape as ShortLinks:ScrambleKey above.
        var guestKey = GuestIdentity.ResolveGuestKey(configuration["GUEST_KEY"]);
        if (string.IsNullOrWhiteSpace(configuration["GUEST_KEY"]))
            Console.Error.WriteLine("WARNING: GUEST_KEY is not configured. Using a random key for this run only.");
        services.AddSingleton(new GuestKeyProvider(guestKey));
        services.AddSingleton<GuestRateLimiter>();

        services.AddDbContext<LinksDbContext>(o => Configure(o, connection));
        services.AddHttpClient<IUrlReputationChecker, WebRiskChecker>(c => c.Timeout = TimeSpan.FromSeconds(3));

        // Spec §7.0: redirects get their own small, fast-timeout pool so a slow creation transaction can
        // never starve (or be starved by) a redirect lookup.
        services.AddSingleton(RedirectsDataSource.Create(connection));

        // Spec §7.3: NOTIFY-based cache invalidation, real but secondary in this single-instance dev
        // environment (see LinkCacheInvalidation.cs's doc comment) - the main "db" pool is reused for it,
        // since it only sends short-lived SELECT pg_notify(...) calls, not the redirect hot path.
        services.AddSingleton(Npgsql.NpgsqlDataSource.Create(connection));
        services.AddSingleton<ILinkCacheInvalidator, PgNotifyLinkCacheInvalidator>();
        services.AddHostedService<LinkCacheInvalidationListener>();

        // Coordinator leases blocks of numbers (which length grows over time, spec §4.2); CodeGenerator
        // (a 10-round keyed Feistel permutation, spec §5) hides their order and encodes them in base 62.
        // The scramble key must never change once codes have been issued for a length (spec §5.3).
        var scrambleKey = configuration["ShortLinks:ScrambleKey"];
        if (string.IsNullOrWhiteSpace(scrambleKey))
        {
            // Dev-only fallback so the app still boots locally. Every restart reshuffles the number
            // space, so already-issued codes may stop decoding correctly - fine for a throwaway dev
            // database, never acceptable once real links exist.
            scrambleKey = Guid.NewGuid().ToString("N");
            Console.Error.WriteLine(
                "WARNING: ShortLinks:ScrambleKey is not configured. Using a random key for this run only. " +
                "Set ShortLinks:ScrambleKey (dotnet user-secrets or an environment variable) before issuing real links.");
        }
        services.AddSingleton<ICodePermutation>(new FeistelPermutation(scrambleKey));
        services.AddSingleton<CodeGenerator>();

        // Spec §5.4: the coordinator never receives the scramble key, only this fingerprint. Recorded
        // once at coordinator init and compared on every lease and by CreationGate's background poll.
        var keyFingerprint = KeyFingerprint.Compute(scrambleKey);
        var coordinatorToken = configuration["Coordinator:Token"] ?? "";

        // CoordinatorLeaseClient is a singleton (it holds the current leased block), but its HttpClient
        // still comes from IHttpClientFactory for pooled connections and DNS refresh.
        var coordinatorUrl = configuration["Coordinator:BaseUrl"] ?? "http://localhost:5085";
        services.AddHttpClient(nameof(CoordinatorLeaseClient), c =>
        {
            c.BaseAddress = new Uri(coordinatorUrl);
            if (!string.IsNullOrEmpty(coordinatorToken))
                c.DefaultRequestHeaders.Authorization = new("Bearer", coordinatorToken);
        });
        services.AddSingleton(sp => new CoordinatorLeaseClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CoordinatorLeaseClient)), keyFingerprint));

        services.AddHttpClient(nameof(CreationGate), c =>
        {
            c.BaseAddress = new Uri(coordinatorUrl);
            if (!string.IsNullOrEmpty(coordinatorToken))
                c.DefaultRequestHeaders.Authorization = new("Bearer", coordinatorToken);
        });
        services.AddSingleton(sp => new CreationGate(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CreationGate)), keyFingerprint));
        services.AddHostedService<CreationGatePoller>();
        services.AddScoped<IValidator<LinkInput>, LinkInputValidator>();
        services.AddScoped<LinkService>();
        services.AddScoped<FolderService>();
        services.AddScoped<BulkLinkImporter>();
        services.AddScoped<LinkResolver>();
        services.AddScoped<ILinkResolver>(sp => sp.GetRequiredService<LinkResolver>());
        services.AddScoped<ILinkDirectory>(sp => sp.GetRequiredService<LinkResolver>());
        services.AddHostedService<PurgeJob>();
        services.AddHostedService<GuestQuotaCleanupJob>();
        services.AddScoped<IDestinationBlocklist, DestinationBlocklist>();
        services.AddScoped<IAliasDenylist, AliasDenylist>();
        services.AddScoped<AliasFreezeChecker>();
        services.AddScoped<TakedownService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ILinkDigestSource, LinkDigestSource>();
        services.AddScoped<IAccountDataEraser, AccountDataEraser>();
        services.AddScoped<ILinkExportSource, LinkExportSource>();
        services.AddHostedService<SafetyRecheckJob>();

        var captchaEnabled = configuration.GetValue("Captcha:Enabled", true);
        var turnstileSecret = configuration["Captcha:Secret"];
        if (captchaEnabled && !string.IsNullOrWhiteSpace(turnstileSecret))
            services.AddHttpClient<ICaptchaVerifier, TurnstileVerifier>(c => c.Timeout = TimeSpan.FromSeconds(3));
        else
            services.AddSingleton<ICaptchaVerifier, NoOpCaptchaVerifier>();

        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<LinksDbContext>("links", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
