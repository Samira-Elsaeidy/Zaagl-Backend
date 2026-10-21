using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Access.Application;

namespace Zaagl.Modules.Access;

public static class AccessModule
{
    public static IServiceCollection AddAccessModule(this IServiceCollection services) =>
        services.AddSingleton<AccessGate>();
}
