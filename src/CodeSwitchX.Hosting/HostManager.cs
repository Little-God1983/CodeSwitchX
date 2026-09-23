using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Hosting;

/// <summary>Launches VS Code per workspace, tracks its window and keeps it docked over the Cab or cloaked.</summary>
public sealed class HostManager
{
    private readonly IWindowEnumerator _windows;
    private readonly IWindowDocker _docker;
    private readonly IVsCodeLauncher _launcher;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<HostManager> _logger;
    private readonly HostManagerOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, HostedWorkspace> _hosted = [];

    public HostManager(IWindowEnumerator windows, IWindowDocker docker, IVsCodeLauncher launcher, IEventBus bus, TimeProvider time,
        ILogger<HostManager> logger, HostManagerOptions? options = null)
    {
        _windows = windows;
        _docker = docker;
        _launcher = launcher;
        _bus = bus;
        _time = time;
        _logger = logger;
        _options = options ?? new HostManagerOptions();
    }

    public IReadOnlyList<HostedWorkspace> All
    {
        get
        {
            lock (_gate)
            {
                return _hosted.Values.ToArray();
            }
        }
    }

    public HostedWorkspace? Get(Guid workspaceId)
    {
        lock (_gate)
        {
            return _hosted.GetValueOrDefault(workspaceId);
        }
    }

    public async Task<HostedWorkspace> OpenAsync(Workspace workspace, CancellationToken ct)
    {
        HostedWorkspace hosted;
        lock (_gate)
        {
            if (!_hosted.TryGetValue(workspace.Id, out hosted!))
            {
                hosted = new HostedWorkspace(workspace.Id);
                _hosted[workspace.Id] = hosted;
            }

            if (hosted.State == HostState.Running && _docker.IsAlive(hosted.Hwnd))
            {
                return hosted;
            }

            if (hosted.State == HostState.Starting)
            {
                return hosted;
            }

            Transition(hosted, HostState.Starting, error: null);
        }

        var displayName = VsCodeLauncher.DisplayNameForMatching(workspace);
        var before = _windows.TopLevelWindows();

        // VS Code is single-instance: asking it to open a folder that is already open only focuses the existing
        // window, so adopt that window instead of waiting for one that will never appear.
        var existing = VsCodeWindowMatcher.FindExisting(before, displayName, _windows.ProcessName);
        if (existing is not null)
        {
            lock (_gate)
            {
                Adopt(hosted, existing);
                Transition(hosted, HostState.Running, error: null);
            }

            _logger.LogInformation("Adopted existing VS Code window {Hwnd} for {Workspace}", existing.Hwnd, workspace.Name);
            return hosted;
        }

        var launch = _launcher.Launch(workspace);
        if (!launch.Started)
        {
            _logger.LogWarning("Launching VS Code for {Workspace} failed: {Error}", workspace.Name, launch.Error);
            lock (_gate)
            {
                Transition(hosted, HostState.Stopped, launch.Error ?? "launch failed");
            }

            return hosted;
        }

        var deadline = _time.GetUtcNow() + _options.DiscoveryTimeout;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            var match = VsCodeWindowMatcher.FindNew(before, _windows.TopLevelWindows(), displayName, _windows.ProcessName);
            if (match is not null)
            {
                lock (_gate)
                {
                    Adopt(hosted, match);
                    // Keep the fresh window out of sight until the Cab docks it, so it never flashes undocked on the desktop.
                    _docker.Cloak(match.Hwnd);
                    Transition(hosted, HostState.Running, error: null);
                }

                _logger.LogInformation("VS Code window {Hwnd} found for {Workspace}", match.Hwnd, workspace.Name);
                return hosted;
            }
        }

        lock (_gate)
        {
            Transition(hosted, HostState.Stopped, $"No VS Code window titled '{displayName}' appeared within {_options.DiscoveryTimeout.TotalSeconds:0}s");
        }

        return hosted;
    }

    public void ShowInCab(Guid workspaceId, ScreenRect rect)
    {
        lock (_gate)
        {
            if (!_hosted.TryGetValue(workspaceId, out var target) || target.State != HostState.Running)
            {
                return;
            }

            foreach (var other in _hosted.Values.Where(h => h != target && h.State == HostState.Running && h.Visible))
            {
                _docker.Cloak(other.Hwnd);
                other.Visible = false;
            }

            target.TargetRect = rect;
            _docker.Uncloak(target.Hwnd);
            _docker.MoveTo(target.Hwnd, rect);
            _docker.BringToFront(target.Hwnd);
            target.Visible = true;
        }
    }

    public void HideAll()
    {
        lock (_gate)
        {
            foreach (var hosted in _hosted.Values.Where(h => h.State == HostState.Running && h.Visible))
            {
                _docker.Cloak(hosted.Hwnd);
                hosted.Visible = false;
            }
        }
    }

    /// <summary>
    /// Uncloaks every hosted window. DWM cloaking outlives this process, so this must run before exit or a
    /// closed CodeSwitchX would leave the user's VS Code windows running but invisible.
    /// </summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var hosted in _hosted.Values.Where(h => h.State == HostState.Running && _docker.IsAlive(h.Hwnd)))
            {
                _docker.Uncloak(hosted.Hwnd);
                hosted.Visible = true;
                hosted.TargetRect = null;
            }
        }
    }

    /// <summary>Called from the WinEvent watcher: put a docked window back if the user dragged it.</summary>
    public void SnapBack(nint hwnd)
    {
        lock (_gate)
        {
            var hosted = _hosted.Values.FirstOrDefault(h => h.Hwnd == hwnd && h.Visible && h.TargetRect is not null);
            if (hosted is null)
            {
                return;
            }

            var current = _docker.GetRect(hwnd);
            if (current is not null && current != hosted.TargetRect)
            {
                _docker.MoveTo(hwnd, hosted.TargetRect!.Value);
            }
        }
    }

    public void PollLiveness()
    {
        lock (_gate)
        {
            foreach (var hosted in _hosted.Values.Where(h => h.State == HostState.Running && !_docker.IsAlive(h.Hwnd)).ToList())
            {
                hosted.Visible = false;
                Transition(hosted, HostState.Stopped, "VS Code window closed");
            }
        }
    }

    /// <summary>Stops tracking a workspace (e.g. it was unregistered); its window is handed back to the desktop uncloaked.</summary>
    public void Forget(Guid workspaceId)
    {
        lock (_gate)
        {
            if (_hosted.Remove(workspaceId, out var hosted) && hosted.State == HostState.Running && _docker.IsAlive(hosted.Hwnd))
            {
                _docker.Uncloak(hosted.Hwnd);
            }
        }
    }

    private static void Adopt(HostedWorkspace hosted, WindowInfo window)
    {
        hosted.Hwnd = window.Hwnd;
        hosted.ProcessId = window.ProcessId;
        hosted.StartedAt = DateTimeOffset.UtcNow;
        hosted.Visible = false;
        hosted.TargetRect = null;
    }

    private void Transition(HostedWorkspace hosted, HostState state, string? error)
    {
        hosted.State = state;
        hosted.Error = error;
        _bus.Publish(new HostStateChanged(hosted.WorkspaceId, state, hosted.Hwnd, error));
    }
}
