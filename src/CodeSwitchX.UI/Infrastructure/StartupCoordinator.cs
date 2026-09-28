using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Data;
using CodeSwitchX.UI.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Ordered startup, run by the host after the writer listens and before any service that publishes to the engine
/// (AppHostTests pins that order): the writer's stored choice, workspaces into the resolver, recent sessions into the
/// engine, then the engine goes live.
/// </summary>
public sealed class StartupCoordinator : IHostedService
{
    public static readonly TimeSpan SessionRestoreWindow = TimeSpan.FromHours(24);

    private readonly WorkspaceRegistry _workspaces;
    private readonly ISessionStore _sessions;
    private readonly ISettingsStore _settings;
    private readonly SessionEngine _engine;
    private readonly PersistenceWriterOptions _writerOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupCoordinator> _logger;

    public StartupCoordinator(WorkspaceRegistry workspaces, ISessionStore sessions, ISettingsStore settings, SessionEngine engine,
        PersistenceWriterOptions writerOptions, TimeProvider time, ILogger<StartupCoordinator> logger)
    {
        _workspaces = workspaces;
        _sessions = sessions;
        _settings = settings;
        _engine = engine;
        _writerOptions = writerOptions;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _writerOptions.StorePayloads = await ReadStorePayloadsAsync(cancellationToken).ConfigureAwait(false);

        var workspaces = await _workspaces.LoadAsync(cancellationToken).ConfigureAwait(false);
        var records = await _sessions.GetActiveSinceAsync(_time.GetUtcNow() - SessionRestoreWindow, cancellationToken).ConfigureAwait(false);
        _engine.Restore(records.Select(r => r.ToSnapshot()));
        _engine.ReResolveWorkspaces();
        _engine.Start();
        _logger.LogInformation("Restored {Workspaces} workspaces and {Sessions} sessions", workspaces.Count, records.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// "Store raw hook payloads" is opt-in and stored; the writer has it before the Event API takes its first event. This
    /// is the one place that reads it: the Settings view shows the writer's flag. A value that cannot be read leaves
    /// payloads off, the privacy-safe side, until the user sets it again.
    /// </summary>
    private async Task<bool> ReadStorePayloadsAsync(CancellationToken ct)
    {
        try
        {
            return await _settings.GetAsync<bool?>(SettingKeys.StorePayloads, ct).ConfigureAwait(false) ?? false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The stored \"store raw hook payloads\" choice could not be read; payloads are not stored");
            return false;
        }
    }
}
