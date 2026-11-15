using Npgsql;

namespace Zaagl.Modules.Links.Infrastructure;

/// <summary>
/// Spec §7.0: a separate, small, fast-timeout connection pool used ONLY by the redirect lookup, so a slow
/// link-creation transaction (which uses the app's normal "main" pool/DbContext) can never starve or
/// queue behind redirects, and vice versa. Wraps a plain NpgsqlDataSource rather than being one itself,
/// so DI doesn't have to disambiguate it from any other NpgsqlDataSource registration. It owns that data source: register it
/// through a factory (<c>AddSingleton(_ => RedirectsDataSource.Create(...))</c>) so the container disposes it, and with it every
/// pooled connection, when the host stops.
/// </summary>
public sealed class RedirectsDataSource(NpgsqlDataSource value) : IDisposable, IAsyncDisposable
{
    public NpgsqlDataSource Value { get; } = value;

    public void Dispose() => Value.Dispose();

    public ValueTask DisposeAsync() => Value.DisposeAsync();

    public static RedirectsDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // 30, or less when the connection string itself caps its pools lower (the test hosts do).
        builder.ConnectionStringBuilder.MaxPoolSize = Math.Min(30, builder.ConnectionStringBuilder.MaxPoolSize);
        builder.ConnectionStringBuilder.Timeout = 2; // connection acquire timeout, seconds
        builder.ConnectionStringBuilder.CommandTimeout = 2;
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        return new RedirectsDataSource(builder.Build());
    }
}
