using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>Marks a session Errored when the claude process recorded from the relay's parent chain disappears.</summary>
public sealed class ProcessLivenessMonitor : BackgroundService
{
    private readonly SessionEngine _engine;
    private readonly IProcessProbe _probe;
    private readonly TimeProvider _time;
    private readonly ILogger<ProcessLivenessMonitor> _logger;

    public ProcessLivenessMonitor(SessionEngine engine, IProcessProbe probe, TimeProvider time, ILogger<ProcessLivenessMonitor> logger)
    {
        _engine = engine;
        _probe = probe;
        _time = time;
        _logger = logger;
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            Tick();
        }
    }

    internal void Tick()
    {
        foreach (var snapshot in _engine.Snapshots)
        {
            if (snapshot.ClaudePid is not { } pid || !SessionStateMachine.IsLive(snapshot.State))
            {
                continue;
            }

            if (!_probe.IsAlive(pid))
            {
                _logger.LogInformation("Process {Pid} for session {SessionId} is gone", pid, snapshot.SessionId);
                _engine.MarkProcessGone(snapshot.SessionId);
            }
        }
    }
}
