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

    /// <summary>The window the Cab shows, or 0; written under the lock, read without it (see <see cref="IsShownInCab"/>).</summary>
    private nint _shownInCab;

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

    /// <summary>
    /// The window the Cab shows has changed: the new one, or 0 for none. Raised under the host lock, on whatever thread
    /// changed it, so a handler must hand the work on, not do it.
    /// </summary>
    public event Action<nint>? ShownInCabChanged;

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
    /// <param name="placement">
    /// Where the Cab wants the window right now, or null once it does not wait for it on screen any more (the user went
    /// back to the Yard, or on to another workspace). A window VS Code opens for this call is put there as it appears,
    /// before its title can tell whose it is: VS Code shows it at once where it last had a window. Without it, the new
    /// window is left where it is until it is known, and then hidden.
    /// </param>
    public async Task<HostedWorkspace> OpenAsync(Workspace workspace, CancellationToken ct, Func<ScreenRect?>? placement = null)
    {
        var displayName = VsCodeLauncher.DisplayNameForMatching(workspace);
        HostedWorkspace hosted;
        Task<HostedWorkspace>? pending = null;
        TaskCompletionSource<HostedWorkspace>? completion = null;
        Discovery? discovery = null;
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
                // A tile opened while its auto-start is still looking for the window: the Cab waits for it from now on.
                existing.Placement ??= placement;
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
                discovery = new Discovery(completion.Task, displayName) { Placement = placement };
                _inflight[workspace.Id] = discovery;
                hosted.DisplayName = displayName;
                hosted.Profile = workspace.VsCodeProfile;
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
            await Task.Run(() => DiscoverAsync(workspace, hosted, discovery!, ct), ct).ConfigureAwait(false);
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
                ReturnUnclaimedLocked(discovery!);
                _inflight.Remove(workspace.Id);
            }

            completion!.SetResult(hosted);
        }

        return hosted;
    }

    private async Task DiscoverAsync(Workspace workspace, HostedWorkspace hosted, Discovery discovery, CancellationToken ct)
    {
        var displayName = hosted.DisplayName;
        var before = _windows.TopLevelWindows();

        // VS Code is single-instance: asking it to open a folder that is already open only focuses the existing
        // window, so adopt that window instead of waiting for one that will never appear. A window another
        // workspace already hosts is never a candidate, however alike the folder names are.
        var existing = VsCodeWindowMatcher.FindAllExisting(NotHostedElsewhere(hosted, before), displayName, _windows.ProcessName);

        // A floating editor window of a docked tile with the same folder name looks like any window of that folder.
        List<WindowInfo> sure = HasDockedNamesake(hosted)
            ? []
            : existing.Where(w => VsCodeWindowMatcher.TitleNamesRoot(w.Title, displayName, workspace.VsCodeProfile)).ToList();
        if (sure.Count == 1)
        {
            Adopt(workspace, hosted, sure[0], hide: false);
            return;
        }

        // A discovery that waited behind a namesake may find its tile removed, or CodeSwitchX closing, by now.
        if (ReasonNotToLaunch(hosted) is { } reason)
        {
            Stop(hosted, reason);
            return;
        }

        // Otherwise the titles cannot tell which window, if any, shows the folder: two windows name it where VS Code
        // writes the folder name (the same folder name in two places, a floating editor window), or a window names it
        // elsewhere (a longer folder name, a profile, an editor tab). VS Code, asked to open the folder, opens a new
        // window or brings forward the one that shows it.
        lock (_gate)
        {
            discovery.Before = before.Select(w => w.Hwnd).ToHashSet();
        }

        var launch = _launcher.Launch(workspace);
        if (!launch.Started)
        {
            _logger.LogWarning("Launching VS Code for {Workspace} failed: {Error}", workspace.Name, launch.Error);
            Stop(hosted, launch.Error ?? "launch failed");
            return;
        }

        var deadline = _time.GetUtcNow() + _options.DiscoveryTimeout;
        WindowInfo? inFrontBefore = null;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            if (!IsTracked(hosted))
            {
                Stop(hosted, "Workspace was removed while VS Code was starting");
                return;
            }

            var windows = _windows.TopLevelWindows();
            // Windows reports a new window within milliseconds (WindowAppeared); this catches one it did not report.
            PlaceNewWindows(discovery, windows);
            var match = VsCodeWindowMatcher.FindNew(before, NotHostedElsewhere(hosted, windows), displayName, _windows.ProcessName);
            if (match is not null)
            {
                // Out of sight until the Cab docks it, so it never stands undocked on the desktop; one that already
                // stands in the Cab, which still waits for it, stays: hidden, it would blink there.
                Adopt(workspace, hosted, match, hide: !StandsInCab(discovery, match.Hwnd));
                return;
            }

            // The window in front counts only once VS Code has answered, and only when it is in front on two polls in
            // a row: a window the user switches to meanwhile is no answer.
            // Taken from the current listing, not the one before the launch: VS Code may have shown the window to answer.
            var inFront = existing.Count > 0 && HasAnswered(launch, before, windows) && _windows.ForegroundWindow() is var foreground
                && existing.Any(w => w.Hwnd == foreground)
                ? windows.FirstOrDefault(w => w.Hwnd == foreground)
                : null;
            if (inFront is not null && inFront.Hwnd == inFrontBefore?.Hwnd)
            {
                Adopt(workspace, hosted, inFront, hide: false);
                return;
            }

            inFrontBefore = inFront;
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
    /// True once VS Code has answered: the Code.exe that was launched has handed the folder over and exited, and no new
    /// VS Code window is still on its way. Then the folder was already open, and VS Code brought its window forward, or
    /// kept it in front when it was there already (a jump hotkey pressed while typing in it).
    /// </summary>
    private bool HasAnswered(LaunchResult launch, IReadOnlyList<WindowInfo> before, IReadOnlyList<WindowInfo> windows)
    {
        if (launch.ProcessId is not { } pid || _windows.ProcessName((uint)pid) is not null)
        {
            return false;
        }

        // VS Code creates the window of a folder it opens before the launched Code.exe exits, hidden and untitled at
        // first. A new window that already shows a folder is not that one, or FindNew would have taken it: another
        // tile's window, or a floating editor.
        var known = before.Select(w => w.Hwnd).ToHashSet();
        return !windows.Any(w => !known.Contains(w.Hwnd) && VsCodeWindowMatcher.IsVsCodeWindow(w, _windows.ProcessName)
            && !(w.IsVisible && VsCodeWindowMatcher.ShowsAFolder(w, _windows.ProcessName)));
    }

    /// <summary>
    /// Called from the WinEvent watcher when a window is created or shown: a VS Code window that comes while the Cab waits
    /// for a new one goes to the Cab at once, instead of standing where VS Code last had a window until its title names
    /// the folder, a second later.
    /// </summary>
    public void WindowAppeared(nint hwnd)
    {
        lock (_gate)
        {
            if (!_inflight.Values.Any(d => d.Placement is not null && d.Before is { } before && !before.Contains(hwnd) && !IsPlacedLocked(hwnd)))
            {
                return;
            }
        }

        // Most windows of the desktop end at the check above; reading one takes several calls into Windows, made unlocked.
        if (_windows.Describe(hwnd) is { } window)
        {
            lock (_gate)
            {
                foreach (var discovery in _inflight.Values)
                {
                    if (TryPlaceLocked(discovery, window))
                    {
                        break;
                    }
                }
            }
        }
    }

    private void PlaceNewWindows(Discovery discovery, IReadOnlyList<WindowInfo> windows)
    {
        lock (_gate)
        {
            foreach (var window in windows)
            {
                TryPlaceLocked(discovery, window);
            }
        }
    }

    /// <summary>
    /// Moves a VS Code window that came after the launch into the Cab while the Cab waits for this discovery's window. Its
    /// title cannot tell yet whose it is, so one that already names another folder is left alone, and where it was is
    /// kept to put back one that turns out not to be it (see <see cref="ReturnUnclaimedLocked"/>).
    /// </summary>
    private bool TryPlaceLocked(Discovery discovery, WindowInfo window)
    {
        if (discovery.Before is not { } before || before.Contains(window.Hwnd) || IsPlacedLocked(window.Hwnd)
            || _hosted.Values.Any(h => h.State == HostState.Running && h.Hwnd == window.Hwnd)
            || !VsCodeWindowMatcher.IsVsCodeWindow(window, _windows.ProcessName)
            || (VsCodeWindowMatcher.ShowsAFolder(window, _windows.ProcessName) && !VsCodeWindowMatcher.TitleNamesWorkspace(window.Title, discovery.DisplayName))
            || discovery.Placement?.Invoke() is not { } rect)
        {
            return false;
        }

        discovery.Placed[window.Hwnd] = new PlacedWindow(_docker.GetRect(window.Hwnd));
        _docker.MoveTo(window.Hwnd, rect);
        return true;
    }

    private bool IsPlacedLocked(nint hwnd) => _inflight.Values.Any(d => d.Placed.ContainsKey(hwnd));

    private bool StandsInCab(Discovery discovery, nint hwnd)
    {
        lock (_gate)
        {
            return discovery.Placed.ContainsKey(hwnd) && discovery.Placement?.Invoke() is not null;
        }
    }

    /// <summary>
    /// A window that rescales itself as it arrives on a monitor with another scale (WM_DPICHANGED) is put back to the
    /// Cab's size, a few times at most: the Cab docks it once it is known.
    /// </summary>
    private void KeepPlacedLocked(nint hwnd)
    {
        foreach (var discovery in _inflight.Values)
        {
            if (discovery.Placed.TryGetValue(hwnd, out var placed) && placed.Moves < PlacedWindow.MoveLimit
                && discovery.Placement?.Invoke() is { } rect && _docker.GetRect(hwnd) is { } current && current != rect)
            {
                placed.Moves++;
                _docker.MoveTo(hwnd, rect);
                return;
            }
        }
    }

    /// <summary>Puts every window this discovery placed in the Cab, and nobody took, back where it was.</summary>
    private void ReturnUnclaimedLocked(Discovery discovery)
    {
        foreach (var (hwnd, placed) in discovery.Placed)
        {
            if (placed.From is { } from && !_hosted.Values.Any(h => h.State == HostState.Running && h.Hwnd == hwnd) && _docker.IsAlive(hwnd))
            {
                _docker.MoveTo(hwnd, from);
            }
        }

        discovery.Placed.Clear();
    }

    private string? ReasonNotToLaunch(HostedWorkspace hosted)
    {
        lock (_gate)
        {
            return !IsTrackedLocked(hosted) ? "Workspace was removed while VS Code was starting"
                : _released ? "CodeSwitchX is closing"
                : null;
        }
    }

    private bool HasDockedNamesake(HostedWorkspace self)
    {
        lock (_gate)
        {
            return _hosted.Values.Any(h => !ReferenceEquals(h, self) && h.State == HostState.Running
                && string.Equals(h.DisplayName, self.DisplayName, StringComparison.OrdinalIgnoreCase));
        }
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
            // A window adopted as it is on the desktop is visible, so HideAll and another tile's ShowInCab hide it; before,
            // it stayed on the desktop until its own tile had shown it once.
            hosted.Visible = !hide && window.IsVisible;
            hosted.TargetRect = null;
            if (hide)
            {
                _docker.Cloak(window.Hwnd);
            }

            Transition(hosted, HostState.Running, error: null);
            return true;
        }
    }

    /// <summary>
    /// Switches the Cab to this workspace: hides the others, shows this window in the rect and raises it; with
    /// <paramref name="focus"/> false it goes on top without taking the foreground (see <see cref="IWindowDocker.PlaceOnTop"/>).
    /// </summary>
    public void ShowInCab(Guid workspaceId, ScreenRect rect, bool focus = true)
    {
        lock (_gate)
        {
            if (_hosted.TryGetValue(workspaceId, out var target) && target.State == HostState.Running)
            {
                ShowInCabLocked(target, rect, focus);
            }
        }
    }

    /// <summary>
    /// Follows the Cab area while the window is already showing in it: a move only, no z-order change, so resizing or
    /// dragging the shell never pulls VS Code over other windows or steals focus. A window that is not visible, or that
    /// was never docked (adopted as it was on the desktop, so visible but behind the shell), is shown as by <see cref="ShowInCab"/>.
    /// </summary>
    public void Dock(Guid workspaceId, ScreenRect rect)
    {
        lock (_gate)
        {
            if (!_hosted.TryGetValue(workspaceId, out var target) || target.State != HostState.Running)
            {
                return;
            }

            if (!target.Visible || target.TargetRect is null)
            {
                ShowInCabLocked(target, rect);
                return;
            }

            target.TargetRect = rect;
            target.ResetSnapBack();
            _docker.MoveTo(target.Hwnd, rect);
        }
    }

    private void ShowInCabLocked(HostedWorkspace target, ScreenRect rect, bool focus = true)
    {
        foreach (var other in _hosted.Values.Where(h => h != target && h.State == HostState.Running && h.Visible))
        {
            _docker.Cloak(other.Hwnd);
            other.Visible = false;
        }

        target.TargetRect = rect;
        target.ResetSnapBack();
        // Moved before it is shown: a fresh window is hidden where VS Code opened it, possibly on another monitor, and
        // shown first it drew a frame there.
        _docker.MoveTo(target.Hwnd, rect);
        _docker.Uncloak(target.Hwnd);
        if (focus)
        {
            _docker.BringToFront(target.Hwnd);
        }
        else
        {
            _docker.PlaceOnTop(target.Hwnd);
        }

        target.Visible = true;
        PublishShownLocked();
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

            PublishShownLocked();
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

            PublishShownLocked();
        }
    }

    /// <summary>
    /// Called from the WinEvent watcher when the user starts to drag or resize a window by its frame: a docked window
    /// does not go. Windows' move loop is ended as it starts, so nothing has moved, and nothing fights: snapped back
    /// after each step instead, the window flipped between the cursor and the Cab at every move of the mouse. The odd
    /// step a fast drag gets in before the cancel lands is <see cref="SnapBack"/>'s.
    /// </summary>
    public void RefuseMoveSize(nint hwnd)
    {
        lock (_gate)
        {
            if (DockedLocked(hwnd) is not null)
            {
                _docker.CancelMoveSize(hwnd);
            }
        }
    }

    /// <summary>
    /// Called from the WinEvent watcher: put a docked window back if something moved it (a maximize, a Win+arrow, the
    /// step of a drag that landed before <see cref="RefuseMoveSize"/>, a drag while Windows refused that hook).
    /// Something that puts the window back in its own place after every snap (a tiling window manager, a second
    /// CodeSwitchX) would move it to and fro for ever, so after <see cref="SnapBackLimit"/> quick snap-backs from the
    /// same place the window is left there until the next dock. A move to a new place is always followed.
    /// </summary>
    public void SnapBack(nint hwnd)
    {
        lock (_gate)
        {
            var hosted = DockedLocked(hwnd);
            if (hosted is null)
            {
                KeepPlacedLocked(hwnd);
                return;
            }

            if (hosted.SnapBackSuspended)
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
            if (hosted.SnapBackSuspended)
            {
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

    /// <summary>
    /// Called from the WinEvent watcher when a window is destroyed: a hosted one closed by the user is known at once, where
    /// <see cref="PollLiveness"/> would find it only on its next round. The event is taken at its word: Windows sends it
    /// while the destruction is still under way, and IsWindow, asked then, may still say yes.
    /// </summary>
    public void WindowDestroyed(nint hwnd)
    {
        lock (_gate)
        {
            foreach (var hosted in _hosted.Values.Where(h => h.State == HostState.Running && h.Hwnd == hwnd).ToList())
            {
                hosted.Visible = false;
                Transition(hosted, HostState.Stopped, "VS Code window closed");
            }
        }
    }

    /// <summary>
    /// True for the window the Cab shows right now. Lock-free, for the mouse hook: it holds every mouse event of the
    /// desktop while it asks, and the lock is held across calls into VS Code.
    /// </summary>
    public bool IsShownInCab(nint hwnd) => hwnd != 0 && hwnd == Volatile.Read(ref _shownInCab);

    /// <summary>Stops tracking a workspace (e.g. it was unregistered); its window is handed back to the desktop visible.</summary>
    public void Forget(Guid workspaceId)
    {
        lock (_gate)
        {
            if (_hosted.Remove(workspaceId, out var hosted) && hosted.State == HostState.Running && _docker.IsAlive(hosted.Hwnd))
            {
                _docker.Uncloak(hosted.Hwnd);
            }

            PublishShownLocked();
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

    /// <summary>The workspace whose window this is, while that window is shown in the Cab and has a place to be kept in.</summary>
    private HostedWorkspace? DockedLocked(nint hwnd) =>
        _hosted.Values.FirstOrDefault(h => h.Hwnd == hwnd && h.Visible && h.TargetRect is not null);

    /// <summary>
    /// The windows no other workspace hosts. A hosted window counts as its host's only while its title still names
    /// that host where VS Code writes the folder name: File > Open Recent opens another folder in the same window,
    /// which then belongs to that folder, even when its name contains the host's ("App - Copy" for "App").
    /// </summary>
    private List<WindowInfo> NotHostedElsewhere(HostedWorkspace self, IReadOnlyList<WindowInfo> windows)
    {
        lock (_gate)
        {
            return windows
                .Where(w => !_hosted.Values.Any(h => !ReferenceEquals(h, self) && h.State == HostState.Running && h.Hwnd == w.Hwnd
                    && VsCodeWindowMatcher.TitleNamesRoot(w.Title, h.DisplayName, h.Profile)))
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
        PublishShownLocked();
        _bus.Publish(new HostStateChanged(hosted.WorkspaceId, state, hosted.Hwnd, error));
    }

    /// <summary>Called after every change to what is Running, visible or docked.</summary>
    private void PublishShownLocked()
    {
        var shown = _hosted.Values.FirstOrDefault(h => h.State == HostState.Running && h.Visible && h.TargetRect is not null)?.Hwnd ?? 0;
        if (shown != _shownInCab)
        {
            Volatile.Write(ref _shownInCab, shown);
            ShownInCabChanged?.Invoke(shown);
        }
    }

    /// <summary>A discovery in flight. Its mutable parts are read and written under the host lock.</summary>
    private sealed class Discovery(Task<HostedWorkspace> task, string displayName)
    {
        public Task<HostedWorkspace> Task { get; } = task;
        public string DisplayName { get; } = displayName;

        /// <summary>Where the Cab wants the window now; null, or returning null, when nobody waits for it on screen.</summary>
        public Func<ScreenRect?>? Placement { get; set; }

        /// <summary>The windows there before VS Code was launched; null until then. Only a window that came after can be the one it opens.</summary>
        public HashSet<nint>? Before { get; set; }

        public Dictionary<nint, PlacedWindow> Placed { get; } = [];
    }

    /// <summary>A window put in the Cab before its title told whose it is, with where it stood before.</summary>
    private sealed class PlacedWindow(ScreenRect? from)
    {
        public const int MoveLimit = 3;

        public ScreenRect? From { get; } = from;
        public int Moves { get; set; }
    }
}
