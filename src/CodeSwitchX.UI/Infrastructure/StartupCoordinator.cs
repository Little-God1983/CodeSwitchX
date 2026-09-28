using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Data;
using CodeSwitchX.UI.Settings;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Ordered startup: workspaces into the resolver, recent sessions into the engine, then the engine goes live.</summary>
public sealed class StartupCoordinator
{
    public static readonly TimeSpan SessionRestoreWindow = TimeSpan.FromHours(24);

    private readonly WorkspaceRegistry _workspaces;
    private readonly ISessionStore _sessions;
    private readonly ISettingsStore _settings;
    private readonly SessionEngine _engine;
    private readonly PersistenceWriter _writer;
    private readonly PersistenceWriterOptions _writerOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupCoordinator> _logger;

    public StartupCoordinator(WorkspaceRegistry workspaces, ISessionStore sessions, ISettingsStore settings, SessionEngine engine,
        PersistenceWriter writer, PersistenceWriterOptions writerOptions, TimeProvider time, ILogger<StartupCoordinator> logger)
    {
        _workspaces = workspaces;
        _sessions = sessions;
        _settings = settings;
        _engine = engine;
        _writer = writer;
        _writerOptions = writerOptions;
        _time = time;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // The writer listens from here, not from the host's start: the engine's sweep timer runs as soon as it starts, and
        // the Event API takes hook events before the shell reads its settings, so the writer needs its subscription and
        // the stored "store payloads" choice before either.
        _writerOptions.StorePayloads = await _settings.GetAsync<bool?>(SettingKeys.StorePayloads, ct).ConfigureAwait(false) ?? false;
        _writer.Subscribe();

        var workspaces = await _workspaces.LoadAsync(ct).ConfigureAwait(false);
        var records = await _sessions.GetActiveSinceAsync(_time.GetUtcNow() - SessionRestoreWindow, ct).ConfigureAwait(false);
        _engine.Restore(records.Select(r => r.ToSnapshot()));
        _engine.ReResolveWorkspaces();
        _engine.Start();
        _logger.LogInformation("Restored {Workspaces} workspaces and {Sessions} sessions", workspaces.Count, records.Count);
    }
}
