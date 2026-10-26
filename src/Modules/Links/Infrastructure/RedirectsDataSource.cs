using Npgsql;

namespace Zaagl.Modules.Links.Infrastructure;

/// <summary>
/// Spec §7.0: a separate, small, fast-timeout connection pool used ONLY by the redirect lookup, so a slow
/// link-creation transaction (which uses the app's normal "main" pool/DbContext) can never starve or
/// queue behind redirects, and vice versa. Wraps a plain NpgsqlDataSource rather than being one itself,
/// so DI doesn't have to disambiguate it from any other NpgsqlDataSource registration.
/// </summary>
public sealed class RedirectsDataSource(NpgsqlDataSource value)
{
    public NpgsqlDataSource Value { get; } = value;

    public static RedirectsDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.MaxPoolSize = 30;
        builder.ConnectionStringBuilder.Timeout = 2; // connection acquire timeout, seconds
        builder.ConnectionStringBuilder.CommandTimeout = 2;
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        return new RedirectsDataSource(builder.Build());
    }
}
