using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Hosting;

/// <summary>Launches VS Code per workspace, tracks its window and keeps it docked over the Cab or hidden.</summary>
public sealed class HostManager : IDisposable
{
    /// <summary>Snap-backs from one and the same place, each within <see cref="SnapBackRepeatWindow"/> of the last, before snapping stops until the next dock.</summary>
    public const int SnapBackLimit = 5;

    private static readonly TimeSpan SnapBackRepeatWindow = TimeSpan.FromSeconds(2);

    private readonly IWindowEnumerator _windows;
    private readonly IWindowDocker _docker;
    private readonly IVsCodeLauncher _launcher;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<HostManager> _logger;
    private readonly HostManagerOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, HostedWorkspace> _hosted = [];
    private readonly Dictionary<Guid, Discovery> _inflight = [];
    private readonly IDisposable _unregistered;
    private bool _released;

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
        var displayName = VsCodeLauncher.DisplayNameForMatching(workspace);
        HostedWorkspace hosted;
        Task<HostedWorkspace>? pending = null;
        TaskCompletionSource<HostedWorkspace>? completion = null;
        Task[] sameName = [];
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
                pending = existing.Task;
            }
            else
            {
                // Two folders with the same name, or names like "App" and "App - Copy", can both be named by one title:
                // while one is still being found, its window would pass for the other's. They are found one by one.
                sameName = _inflight.Values
                    .Where(d => VsCodeWindowMatcher.NamesOverlap(d.DisplayName, displayName))
                    .Select(d => (Task)d.Task)
                    .ToArray();
                completion = new TaskCompletionSource<HostedWorkspace>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflight[workspace.Id] = new Discovery(completion.Task, displayName);
                hosted.DisplayName = displayName;
                Transition(hosted, HostState.Starting, error: null);
            }
        }

        if (pending is not null)
        {
            return await pending.ConfigureAwait(false);
        }

        try
        {
            // These tasks never fault: every discovery ends with SetResult.
            await Task.WhenAll(sameName).WaitAsync(ct).ConfigureAwait(false);

            // Off the caller's thread, which is the UI thread: checking a folder on a network share that is offline
            // takes about 20 s, and the shell must not freeze meanwhile.
            await Task.Run(() => DiscoverAsync(workspace, hosted, ct), ct).ConfigureAwait(false);
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
        var displayName = hosted.DisplayName;
        var before = _windows.TopLevelWindows();

        // VS Code is single-instance: asking it to open a folder that is already open only focuses the existing
        // window, so adopt that window instead of waiting for one that will never appear. A window another
        // workspace already hosts is never a candidate, however alike the folder names are.
        var existing = VsCodeWindowMatcher.FindAllExisting(NotHostedElsewhere(hosted, before), displayName, _windows.ProcessName);
        var sure = existing.Where(w => VsCodeWindowMatcher.TitleNamesRoot(w.Title, displayName, workspace.VsCodeProfile)).ToList();
        if (sure.Count == 1)
        {
            Adopt(workspace, hosted, sure[0], hide: false);
            return;
        }

        // Otherwise the titles cannot tell which window, if any, shows the folder: two windows name it where VS Code
        // writes the folder name (the same folder name in two places, a floating editor window), or a window names it
        // elsewhere (a longer folder name, a profile, an editor tab). VS Code, asked to open the folder, opens a new
        // window or brings forward the one that shows it.
        var foregroundBefore = existing.Count > 0 ? _windows.ForegroundWindow() : 0;
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

            var match = VsCodeWindowMatcher.FindNew(before, NotHostedElsewhere(hosted, _windows.TopLevelWindows()), displayName, _windows.ProcessName);
            if (match is not null)
            {
                // Keep the fresh window out of sight until the Cab docks it, so it never flashes undocked on the desktop.
                Adopt(workspace, hosted, match, hide: true);
                return;
            }

            if (existing.Count > 0 && _windows.ForegroundWindow() is var foreground
                && existing.FirstOrDefault(w => w.Hwnd == foreground) is { } broughtForward
                && (foreground != foregroundBefore || HasAnswered(launch, before)))
            {
                Adopt(workspace, hosted, broughtForward, hide: false);
                return;
            }
        }

        var seconds = _options.DiscoveryTimeout.TotalSeconds;
        Stop(hosted, existing.Count switch
        {
            0 => $"No VS Code window titled '{displayName}' appeared within {seconds:0}s",
            1 => $"A VS Code window has '{displayName}' in its title, but VS Code neither opened {workspace.RootPath} nor brought that window forward within {seconds:0}s",
            _ => $"{existing.Count} VS Code windows have '{displayName}' in their titles, and VS Code did not bring the one for {workspace.RootPath} forward within {seconds:0}s",
        });
    }

    /// <summary>
    /// True once the Code.exe that was launched has handed the folder to the running VS Code and exited, and VS Code
    /// opened no new window for it. Then the folder was already open, and VS Code focused its window: when that
    /// window was in front already (a jump hotkey pressed while typing in it), the foreground did not change.
    /// </summary>
    private bool HasAnswered(LaunchResult launch, IReadOnlyList<WindowInfo> before)
    {
        if (launch.ProcessId is not { } pid || _windows.ProcessName((uint)pid) is not null)
        {
            return false;
        }

        // VS Code creates the window of a folder it opens before the launched Code.exe exits, even while it is hidden.
        var known = before.Select(w => w.Hwnd).ToHashSet();
        return !_windows.TopLevelWindows().Any(w => !known.Contains(w.Hwnd)
            && string.Equals(w.ClassName, VsCodeWindowMatcher.ElectronClass, StringComparison.Ordinal)
            && string.Equals(_windows.ProcessName(w.ProcessId), VsCodeWindowMatcher.ProcessName, StringComparison.OrdinalIgnoreCase));
    }

    private void Adopt(Workspace workspace, HostedWorkspace hosted, WindowInfo window, bool hide)
    {
        // Windows (UIPI) ignores a process that is not elevated when it moves, shows or hides an elevated one's window:
        // the tile would say Running while its Cab stayed empty and Back to Yard left VS Code on the desktop.
        if (_docker.IsOutOfReach(window.Hwnd))
        {
            Stop(hosted, "VS Code runs as administrator, so Windows does not let CodeSwitchX move or hide its window. Start VS Code without administrator rights, or CodeSwitchX with them.");
            return;
        }

        if (TryAdopt(hosted, window, hide))
        {
            _logger.LogInformation("VS Code window {Hwnd} adopted for {Workspace}", window.Hwnd, workspace.Name);
        }
    }

    /// <summary>
    /// Records the window as Running, unless the workspace was forgotten meanwhile or CodeSwitchX is closing: then the
    /// window is left alone and visible.
    /// </summary>
    private bool TryAdopt(HostedWorkspace hosted, WindowInfo window, bool hide)
    {
        lock (_gate)
        {
            if (!IsTrackedLocked(hosted))
            {
                Transition(hosted, HostState.Stopped, "Workspace was removed while VS Code was starting");
                return false;
            }

            // After ReleaseAll nothing may hide a window again: this process is about to exit and would leave it invisible.
            if (_released)
            {
                Transition(hosted, HostState.Stopped, "CodeSwitchX is closing");
                return false;
            }

            // A hosted window still names its host, or it would not be a candidate: this one was switched to our
            // folder (File > Open Recent opens it in the same window), so its old tile no longer shows it.
            foreach (var previous in _hosted.Values.Where(h => h != hosted && h.State == HostState.Running && h.Hwnd == window.Hwnd).ToList())
            {
                previous.Visible = false;
                Transition(previous, HostState.Stopped, $"Its VS Code window now shows {hosted.DisplayName}");
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
            target.ResetSnapBack();
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
        target.ResetSnapBack();
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
            _released = true;
            foreach (var hosted in _hosted.Values.Where(h => h.State == HostState.Running && _docker.IsAlive(h.Hwnd)))
            {
                _docker.Uncloak(hosted.Hwnd);
                hosted.Visible = true;
                hosted.TargetRect = null;
            }
        }
    }

    /// <summary>
    /// Called from the WinEvent watcher: put a docked window back if the user dragged it. Something that puts the
    /// window back in its own place after every snap (a tiling window manager, a second CodeSwitchX) would move it to
    /// and fro for ever, so after <see cref="SnapBackLimit"/> quick snap-backs from the same place the window is left
    /// there until the next dock. A drag reaches a new place every time and is always followed.
    /// </summary>
    public void SnapBack(nint hwnd)
    {
        lock (_gate)
        {
            var hosted = _hosted.Values.FirstOrDefault(h => h.Hwnd == hwnd && h.Visible && h.TargetRect is not null);
            if (hosted is null || hosted.SnapBackSuspended)
            {
                return;
            }

            var current = _docker.GetRect(hwnd);
            if (current is null || current == hosted.TargetRect)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var repeat = current == hosted.SnapBackFrom && now - hosted.LastSnapBackAt < SnapBackRepeatWindow;
            hosted.SnapBackRepeats = repeat ? hosted.SnapBackRepeats + 1 : 1;
            hosted.SnapBackFrom = current;
            hosted.LastSnapBackAt = now;
            if (hosted.SnapBackRepeats > SnapBackLimit)
            {
                hosted.SnapBackSuspended = true;
                _logger.LogWarning("Stopped snapping VS Code window {Hwnd} back until the next dock: something keeps moving it to {Rect}", hwnd, current);
                return;
            }

            _docker.MoveTo(hwnd, hosted.TargetRect!.Value);
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

    /// <summary>
    /// Shows the VS Code windows an earlier run left hidden. A run that ends without <see cref="ReleaseAll"/> (a crash,
    /// End task) leaves them running but invisible, and nothing else ever shows them again. Only CodeSwitchX hides a
    /// VS Code window that shows a folder; windows this run hides itself are left alone.
    /// </summary>
    public void ShowOrphanedWindows()
    {
        var hidden = _windows.TopLevelWindows().Where(w => !w.IsVisible && VsCodeWindowMatcher.ShowsAFolder(w, _windows.ProcessName)).ToList();
        lock (_gate)
        {
            foreach (var window in hidden.Where(w => !_hosted.Values.Any(h => h.State == HostState.Running && h.Hwnd == w.Hwnd)))
            {
                _docker.Uncloak(window.Hwnd);
                _logger.LogInformation("Showed VS Code window {Hwnd} '{Title}', left hidden by an earlier run", window.Hwnd, window.Title);
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

    /// <summary>
    /// The windows no other workspace hosts. A hosted window counts as its host's only while its title still names
    /// that host: File > Open Recent opens another folder in the same window, which then belongs to that folder.
    /// </summary>
    private List<WindowInfo> NotHostedElsewhere(HostedWorkspace self, IReadOnlyList<WindowInfo> windows)
    {
        lock (_gate)
        {
            return windows
                .Where(w => !_hosted.Values.Any(h => !ReferenceEquals(h, self) && h.State == HostState.Running && h.Hwnd == w.Hwnd
                    && VsCodeWindowMatcher.TitleNamesWorkspace(w.Title, h.DisplayName)))
                .ToList();
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

    private sealed record Discovery(Task<HostedWorkspace> Task, string DisplayName);
}
