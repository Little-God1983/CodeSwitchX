using CodeSwitchX.Core;
using CodeSwitchX.Core.Workspaces;
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
        services.AddSingleton<IIdeWindows>(sp => new ClaudeIdeWindows(sp.GetRequiredService<ClaudeCodePaths>()));
        services.AddSingleton<HostManagerOptions>();
        services.AddSingleton<HostManager>();
        services.AddHostedService<HiddenWindowSweep>();
        return services;
    }
}
