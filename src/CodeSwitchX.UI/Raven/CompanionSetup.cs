using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Hosting.VsCode.Companion;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// At startup, in the background, puts back any folder settings a voice start left set when the app ended in its middle,
/// then puts the shipped companion into every VS Code profile the Yard's workspaces open with,
/// where it is missing or another version, and says so in Raven's log. Raven opens chats in VS Code through it.
/// </summary>
public sealed class CompanionSetup : IHostedService
{
    private readonly ICompanionInstaller _installer;
    private readonly IWorkspaceStore _store;
    private readonly string _pendingSettings;
    private readonly IUiDispatcher _ui;
    private readonly Func<RavenPanelViewModel> _raven;
    private readonly ILogger<CompanionSetup> _logger;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>When this run of CodeSwitchX began: notes written since are its own starts', not left over.</summary>
    private static DateTime RunStarted
    {
        get
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            return me.StartTime.ToUniversalTime();
        }
    }

    /// <param name="pendingSettings">Where a start that never ended left what puts a folder's settings back (<see cref="StartSettings"/>).</param>
    /// <param name="raven">Raven's panel; asked for when there is something to say, as it is made after this starts.</param>
    public CompanionSetup(ICompanionInstaller installer, IWorkspaceStore store, string pendingSettings, IUiDispatcher ui, Func<RavenPanelViewModel> raven,
        ILogger<CompanionSetup> logger)
    {
        _installer = installer;
        _store = store;
        _pendingSettings = pendingSettings;
        _ui = ui;
        _raven = raven;
        _logger = logger;
    }

    /// <summary>The setup under way; done once it has said what it did.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Running = Task.Run(() => SetUpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    private async Task SetUpAsync(CancellationToken ct)
    {
        try
        {
            // A run that ended in the middle of a voice start left a folder with Raven's model in its settings.
            if (StartSettings.RecoverAll(_pendingSettings, RunStarted) is [_, ..] stuck)
            {
                var files = string.Join(", ", stuck);
                _ui.Post(() => _raven().Warn($"Raven set a model in {files} for a chat that was starting when CodeSwitchX ended, and could not put it back. Check the file."));
            }

            var workspaces = await _store.GetAllAsync(ct).ConfigureAwait(false);
            var profiles = workspaces.Select(w => w.VsCodeProfile).DefaultIfEmpty(null);
            var result = await _installer.EnsureAsync(profiles, ct).ConfigureAwait(false);
            if (result.Installed.Count > 0)
            {
                var into = string.Join(", ", result.Installed);
                _ui.Post(() => _raven().Note($"Installed the CodeSwitchX companion into VS Code ({into} profile): Raven opens chats there through it."));
            }

            if (result.Failed.Count > 0)
            {
                var why = string.Join("; ", result.Failed);
                _ui.Post(() => _raven().Warn($"The CodeSwitchX companion could not be installed into VS Code ({why}). Raven cannot open chats there until it is."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // closing
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setting up the VS Code companion failed");
        }
    }
}
