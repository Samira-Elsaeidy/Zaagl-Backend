using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Zaagl.SharedKernel.Persistence;

public sealed class ModuleMigrator<TContext>(string module, IServiceScopeFactory scopes) : IModuleMigrator
    where TContext : DbContext
{
    public string Module { get; } = module;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
