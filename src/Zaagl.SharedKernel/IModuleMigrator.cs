namespace Zaagl.SharedKernel;

/// <summary>Implemented by each module that owns a database schema; run at startup when migrations are enabled.</summary>
public interface IModuleMigrator
{
    string Module { get; }
    Task MigrateAsync(CancellationToken cancellationToken);
}
