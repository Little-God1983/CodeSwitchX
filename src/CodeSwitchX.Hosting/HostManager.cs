using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Hosting;

/// <summary>Launches VS Code per workspace, tracks its window and keeps it docked over the Cab or hidden.</summary>
public sealed class HostManager : IDisposable
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
    private readonly Dictionary<Guid, Task<HostedWorkspace>> _inflight = [];
    private readonly IDisposable _unregistered;

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
        // An unregistered workspace must hand its window back to the desktop instead of staying hidden and tracked.
        _unregistered = _bus.Subscribe<WorkspaceUnregistered>(m => Forget(m.WorkspaceId));
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

    /// <summary>
    /// Starts (or adopts) VS Code for the workspace and waits for its window. Concurrent callers (AutoStart plus a
    /// tile click, a double-click, a repeated hotkey) share the discovery in flight and all see its outcome. Whatever
    /// goes wrong, the record never stays in <see cref="HostState.Starting"/>: it ends Running or Stopped with the
    /// reason, so the tile can always be opened again.
    /// </summary>
    public async Task<HostedWorkspace> OpenAsync(Workspace workspace, CancellationToken ct)
    {
        HostedWorkspace hosted;
        Task<HostedWorkspace>? pending = null;
        TaskCompletionSource<HostedWorkspace>? completion = null;
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

            if (_inflight.TryGetValue(workspace.Id, out var existing))
            {
                pending = existing;
            }
            else
            {
                completion = new TaskCompletionSource<HostedWorkspace>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflight[workspace.Id] = completion.Task;
                Transition(hosted, HostState.Starting, error: null);
            }
        }

        if (pending is not null)
        {
            return await pending.ConfigureAwait(false);
        }

        try
        {
            await DiscoverAsync(workspace, hosted, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop(hosted, "Opening was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Workspace} failed", workspace.Name);
            Stop(hosted, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _inflight.Remove(workspace.Id);
            }

            completion!.SetResult(hosted);
        }

        return hosted;
    }

    private async Task DiscoverAsync(Workspace workspace, HostedWorkspace hosted, CancellationToken ct)
    {
        var displayName = VsCodeLauncher.DisplayNameForMatching(workspace);
        var before = _windows.TopLevelWindows();

        // VS Code is single-instance: asking it to open a folder that is already open only focuses the existing
        // window, so adopt that window instead of waiting for one that will never appear. A window another
        // workspace already hosts is never a candidate, however alike the folder names are.
        var candidates = before.Where(w => !IsHostedElsewhere(hosted, w.Hwnd)).ToList();
        var existing = VsCodeWindowMatcher.FindExisting(candidates, displayName, _windows.ProcessName);
        if (existing is not null)
        {
            if (TryAdopt(hosted, existing, hide: false))
            {
                _logger.LogInformation("Adopted existing VS Code window {Hwnd} for {Workspace}", existing.Hwnd, workspace.Name);
            }

            return;
        }

        var launch = _launcher.Launch(workspace);
        if (!launch.Started)
        {
            _logger.LogWarning("Launching VS Code for {Workspace} failed: {Error}", workspace.Name, launch.Error);
            Stop(hosted, launch.Error ?? "launch failed");
            return;
        }

        var deadline = _time.GetUtcNow() + _options.DiscoveryTimeout;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            if (!IsTracked(hosted))
            {
                Stop(hosted, "Workspace was removed while VS Code was starting");
                return;
            }

            var match = VsCodeWindowMatcher.FindNew(before, _windows.TopLevelWindows(), displayName, _windows.ProcessName);
            if (match is not null)
            {
                // Keep the fresh window out of sight until the Cab docks it, so it never flashes undocked on the desktop.
                if (TryAdopt(hosted, match, hide: true))
                {
                    _logger.LogInformation("VS Code window {Hwnd} found for {Workspace}", match.Hwnd, workspace.Name);
                }

                return;
            }
        }

        Stop(hosted, $"No VS Code window titled '{displayName}' appeared within {_options.DiscoveryTimeout.TotalSeconds:0}s");
    }

    /// <summary>Records the window as Running, unless the workspace was forgotten meanwhile: then the window is left alone and visible.</summary>
    private bool TryAdopt(HostedWorkspace hosted, WindowInfo window, bool hide)
    {
        lock (_gate)
        {
            if (!IsTrackedLocked(hosted))
            {
                Transition(hosted, HostState.Stopped, "Workspace was removed while VS Code was starting");
                return false;
            }

            hosted.Hwnd = window.Hwnd;
            hosted.ProcessId = window.ProcessId;
            hosted.StartedAt = DateTimeOffset.UtcNow;
            hosted.Visible = false;
            hosted.TargetRect = null;
            if (hide)
            {
                _docker.Cloak(window.Hwnd);
            }

            Transition(hosted, HostState.Running, error: null);
            return true;
        }
    }

    /// <summary>Switches the Cab to this workspace: hides the others, shows this window in the rect and raises it.</summary>
    public void ShowInCab(Guid workspaceId, ScreenRect rect)
    {
        lock (_gate)
        {
            if (_hosted.TryGetValue(workspaceId, out var target) && target.State == HostState.Running)
            {
                ShowInCabLocked(target, rect);
            }
        }
    }

    /// <summary>
    /// Follows the Cab area while the window is already showing: a move only, no z-order change, so resizing or
    /// dragging the shell never pulls VS Code over other windows or steals focus. A window that is not visible yet is
    /// shown as by <see cref="ShowInCab"/>.
    /// </summary>
    public void Dock(Guid workspaceId, ScreenRect rect)
    {
        lock (_gate)
        {
            if (!_hosted.TryGetValue(workspaceId, out var target) || target.State != HostState.Running)
            {
                return;
            }

            if (!target.Visible)
            {
                ShowInCabLocked(target, rect);
                return;
            }

            target.TargetRect = rect;
            _docker.MoveTo(target.Hwnd, rect);
        }
    }

    private void ShowInCabLocked(HostedWorkspace target, ScreenRect rect)
    {
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
    /// Shows every hosted window again. Hidden windows outlive this process, so this must run before exit or a
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

    /// <summary>Stops tracking a workspace (e.g. it was unregistered); its window is handed back to the desktop visible.</summary>
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

    public void Dispose() => _unregistered.Dispose();

    private bool IsTracked(HostedWorkspace hosted)
    {
        lock (_gate)
        {
            return IsTrackedLocked(hosted);
        }
    }

    private bool IsTrackedLocked(HostedWorkspace hosted) =>
        _hosted.TryGetValue(hosted.WorkspaceId, out var tracked) && ReferenceEquals(tracked, hosted);

    private bool IsHostedElsewhere(HostedWorkspace self, nint hwnd)
    {
        lock (_gate)
        {
            return _hosted.Values.Any(h => !ReferenceEquals(h, self) && h.State == HostState.Running && h.Hwnd == hwnd);
        }
    }

    private void Stop(HostedWorkspace hosted, string error)
    {
        lock (_gate)
        {
            if (hosted.State != HostState.Stopped)
            {
                Transition(hosted, HostState.Stopped, error);
            }
        }
    }

    private void Transition(HostedWorkspace hosted, HostState state, string? error)
    {
        hosted.State = state;
        hosted.Error = error;
        _bus.Publish(new HostStateChanged(hosted.WorkspaceId, state, hosted.Hwnd, error));
    }
}
