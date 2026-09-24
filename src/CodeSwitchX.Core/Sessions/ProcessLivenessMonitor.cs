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
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // One bad tick must not end liveness detection for the rest of the process lifetime.
                _logger.LogError(ex, "Liveness tick failed; retrying on the next interval");
            }
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

            bool alive;
            try
            {
                alive = _probe.IsAlive(pid);
            }
            catch (Exception ex)
            {
                // A PID we cannot query (access denied, PID reused by a protected process) is not evidence of death.
                _logger.LogDebug(ex, "Cannot query process {Pid} for session {SessionId}; leaving it alone", pid, snapshot.SessionId);
                continue;
            }

            if (!alive)
            {
                _logger.LogInformation("Process {Pid} for session {SessionId} is gone", pid, snapshot.SessionId);
                _engine.MarkProcessGone(snapshot.SessionId);
            }
        }
    }
}
