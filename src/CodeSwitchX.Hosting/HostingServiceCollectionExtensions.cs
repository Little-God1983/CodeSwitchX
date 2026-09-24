using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Hosting;

public static class HostingServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXHosting(this IServiceCollection services)
    {
        services.AddSingleton<IWindowEnumerator, Win32WindowEnumerator>();
        services.AddSingleton<IWindowDocker, SnapWindowDocker>();
        services.AddSingleton<IVsCodeLauncher>(_ => new VsCodeLauncher());
        services.AddSingleton<HostManagerOptions>();
        services.AddSingleton<HostManager>();
        return services;
    }
}
