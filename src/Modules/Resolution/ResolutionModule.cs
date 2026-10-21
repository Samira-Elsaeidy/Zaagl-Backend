using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Resolution.Application;

namespace Zaagl.Modules.Resolution;

public static class ResolutionModule
{
    public static IServiceCollection AddResolutionModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResolutionOptions>(configuration.GetSection(ResolutionOptions.Section));
        services.Configure<GeoIpOptions>(configuration.GetSection(GeoIpOptions.Section));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ClientClassifier>();
        services.AddSingleton<IGeoLookup>(sp =>
        {
            var path = configuration[$"{GeoIpOptions.Section}:{nameof(GeoIpOptions.DatabasePath)}"];
            var logger = sp.GetRequiredService<ILogger<MaxMindGeoLookup>>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                logger.LogInformation("No GeoIP database configured; using the CF-IPCountry header only");
                return new NullGeoLookup();
            }
            return new MaxMindGeoLookup(path, logger);
        });
        services.AddScoped<ResolutionService>();
        return services;
    }
}
