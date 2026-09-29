using CodeSwitchX.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Hosting;

/// <summary>At startup, before anything is opened, shows the VS Code windows an earlier run left hidden (see <see cref="HostManager.ShowOrphanedWindows"/>).</summary>
public sealed class HiddenWindowSweep : IHostedService
{
    private readonly HostManager _host;
    private readonly ILogger<HiddenWindowSweep> _logger;
    private readonly Func<bool> _anotherInstanceRuns;

    public HiddenWindowSweep(HostManager host, ILogger<HiddenWindowSweep> logger, Func<bool>? anotherInstanceRuns = null)
    {
        _host = host;
        _logger = logger;
        _anotherInstanceRuns = anotherInstanceRuns ?? AnotherInstanceRuns;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A clean-up: whatever goes wrong here must not stop CodeSwitchX from starting.
        try
        {
            // A second start ends before this runs (SingleInstance); this guards a start that went on without the claim.
            // Only an instance in this session can have hidden this session's windows.
            if (_anotherInstanceRuns())
            {
                _logger.LogInformation("Another CodeSwitchX is running: the hidden VS Code windows are left to it");
                return Task.CompletedTask;
            }

            _host.ShowOrphanedWindows();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Showing the VS Code windows an earlier run left hidden failed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool AnotherInstanceRuns() => ProcessNamesakes.InThisSession().Count > 0;
}
