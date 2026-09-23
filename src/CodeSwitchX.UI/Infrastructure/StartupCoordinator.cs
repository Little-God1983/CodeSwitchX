using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Ordered startup: workspaces into the resolver, recent sessions into the engine, then the engine goes live.</summary>
public sealed class StartupCoordinator
{
    public static readonly TimeSpan SessionRestoreWindow = TimeSpan.FromHours(24);

    private readonly WorkspaceRegistry _workspaces;
    private readonly ISessionStore _sessions;
    private readonly SessionEngine _engine;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupCoordinator> _logger;

    public StartupCoordinator(WorkspaceRegistry workspaces, ISessionStore sessions, SessionEngine engine, TimeProvider time, ILogger<StartupCoordinator> logger)
    {
        _workspaces = workspaces;
        _sessions = sessions;
        _engine = engine;
        _time = time;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var workspaces = await _workspaces.LoadAsync(ct).ConfigureAwait(false);
        var records = await _sessions.GetActiveSinceAsync(_time.GetUtcNow() - SessionRestoreWindow, ct).ConfigureAwait(false);
        _engine.Restore(records.Select(r => r.ToSnapshot()));
        _engine.ReResolveWorkspaces();
        _engine.Start();
        _logger.LogInformation("Restored {Workspaces} workspaces and {Sessions} sessions", workspaces.Count, records.Count);
    }
}
