using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Zaagl.Modules.Links.Infrastructure;

internal static class NpgsqlDataSourceRegistration
{
    /// <summary>
    /// Registers the host's shared <see cref="NpgsqlDataSource"/> (the NOTIFY cache invalidator and its listener) once. It is
    /// built by a factory, so the container owns it and disposes it - closing every pooled connection - when the host stops.
    /// An instance registration (<c>AddSingleton(NpgsqlDataSource.Create(...))</c>) is never disposed by the container: each
    /// host that stopped left its pool open, and in a test run, where many hosts start and stop, the abandoned connections
    /// piled up until PostgreSQL refused new clients (53300). TryAdd: the Api and the worker slice may both ask for it.
    /// </summary>
    public static IServiceCollection AddNpgsqlDataSourceOwnedByHost(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        return services;
    }
}
