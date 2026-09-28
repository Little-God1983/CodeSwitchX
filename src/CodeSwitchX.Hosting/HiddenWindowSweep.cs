using Microsoft.Extensions.Hosting;

namespace CodeSwitchX.Hosting;

/// <summary>At startup, before anything is opened, shows the VS Code windows an earlier run left hidden (see <see cref="HostManager.ShowOrphanedWindows"/>).</summary>
public sealed class HiddenWindowSweep : IHostedService
{
    private readonly HostManager _host;

    public HiddenWindowSweep(HostManager host)
    {
        _host = host;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _host.ShowOrphanedWindows();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
