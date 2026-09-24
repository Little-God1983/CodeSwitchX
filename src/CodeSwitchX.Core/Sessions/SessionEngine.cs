using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// Reconciles hook events, transcript facts and liveness into one <see cref="SessionSnapshot"/> per chat.
/// Every change is stamped with a monotonic <see cref="SessionSnapshot.Version"/> and published while the engine
/// lock is held, so subscribers always receive the snapshots of a session in the order they were produced.
/// </summary>
public sealed class SessionEngine : IDisposable
{
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "sh", "bash", "zsh", "conhost", "wt",
    };

    private static readonly HashSet<string> ClaudeNames = new(StringComparer.OrdinalIgnoreCase) { "claude", "node" };

    private readonly IEventBus _bus;
    private readonly IWorkspaceResolver _resolver;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionEngine> _logger;
    private readonly SessionEngineOptions _options;
    private readonly IProcessProbe? _probe;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private ITimer? _sweepTimer;
    private long _version;

    public SessionEngine(IEventBus bus, IWorkspaceResolver resolver, TimeProvider time, ILogger<SessionEngine> logger,
        SessionEngineOptions? options = null, IProcessProbe? probe = null)
    {
        _bus = bus;
        _resolver = resolver;
        _time = time;
        _logger = logger;
        _options = options ?? new SessionEngineOptions();
        _probe = probe;
    }

    public IReadOnlyCollection<SessionSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.ToArray();
            }
        }
    }

    public SessionSnapshot? Get(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>
    /// Loads persisted snapshots without publishing. Hook evidence does not survive a restart (<see cref="SessionSnapshot.HookSeen"/>
    /// resets). A saved claude PID is checked once: if that process is gone the chat's turn ended while the app was down,
    /// so it comes back Idle rather than as a red Errored row, and the PID is forgotten. A process that started after the
    /// chat's last event holds a reused PID and counts as gone.
    /// A Working session that has been quiet longer than the inferred idle window drops to Idle, because its Stop
    /// hook most likely fired while the app was down. Waiting sessions with a live process are left alone: a permission
    /// prompt is quiet by nature, and the liveness monitor decides when such a chat is really gone.
    /// </summary>
    public void Restore(IEnumerable<SessionSnapshot> persisted)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var snapshot in persisted)
            {
                var restored = snapshot with { HookSeen = false };
                if (restored.ClaudePid is { } pid && !ProcessStillRuns(pid, restored.LastEventAt))
                {
                    restored = restored with { ClaudePid = null };
                    if (restored.State is SessionState.Working or SessionState.Waiting or SessionState.Starting)
                    {
                        restored = restored with { State = SessionState.Idle, StateSince = now };
                    }
                }

                if (restored.State == SessionState.Working && now - restored.LastEventAt > _options.InferredIdleAfter)
                {
                    restored = restored with { State = SessionState.Idle, StateSince = now };
                }

                _sessions[restored.SessionId] = restored;
            }
        }
    }

    public void Start()
    {
        _subscriptions.Add(_bus.Subscribe<HookEventReceived>(m => Apply(m.Event)));
        _subscriptions.Add(_bus.Subscribe<TranscriptUpdated>(m => Apply(m.Update)));
        _subscriptions.Add(_bus.Subscribe<WorkspaceRootsChanged>(_ => ReResolveWorkspaces()));
        _sweepTimer = _time.CreateTimer(_ => SweepStale(), null, _options.SweepInterval, _options.SweepInterval);
    }

    public void Apply(HookEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        lock (_gate)
        {
            var previous = _sessions.GetValueOrDefault(e.SessionId);
            var s = previous ?? NewSession(e.SessionId, e.At);

            var state = s.State;
            // idle_prompt reports a quiet minute. Activity within the quiet window means it raced the next prompt (each relay
            // may take up to a second to land) and describes the turn before, which has already ended.
            var staleIdlePrompt = e.Signal == SessionSignal.IdlePrompt && e.At - s.LastEventAt < _options.InferredIdleAfter;
            if (e.Signal is { } signal && !staleIdlePrompt && SessionStateMachine.TryNext(state, signal, out var next))
            {
                state = next;
            }
            else if (e.Signal is null)
            {
                _logger.LogInformation("Unknown hook event {EventName} for session {SessionId}", e.EventName, e.SessionId);
            }

            var cwd = e.Cwd ?? s.Cwd;
            var awaitingToolResult = e.EventName switch
            {
                "PreToolUse" or "PermissionRequest" => true,
                "PostToolUse" => false,
                _ => s.AwaitingToolResult,
            };
            Commit(previous, s with
            {
                State = state,
                StateSince = state == s.State && previous is not null ? s.StateSince : e.At,
                LastEventAt = e.At > s.LastEventAt ? e.At : s.LastEventAt,
                Cwd = cwd,
                WorkspaceId = cwd != s.Cwd || s.WorkspaceId is null ? _resolver.Resolve(cwd) : s.WorkspaceId,
                TranscriptPath = e.TranscriptPath ?? s.TranscriptPath,
                Model = e.Model ?? s.Model,
                LastToolName = e.ToolName ?? s.LastToolName,
                LastNotification = e.Signal == SessionSignal.Notification ? e.Message ?? e.NotificationType : s.LastNotification,
                Title = s.Title ?? ChatTitle.FromPrompt(e.Prompt, _options.TitleMaxLength),
                Inferred = false,
                HookSeen = true,
                AwaitingToolResult = awaitingToolResult,
                ClaudePid = PickClaudePid(e.ParentChain) ?? s.ClaudePid,
            });
        }
    }

    public void Apply(TranscriptUpdate u)
    {
        ArgumentNullException.ThrowIfNull(u);
        lock (_gate)
        {
            var previous = _sessions.GetValueOrDefault(u.SessionId);
            if (previous is null && u.Historical)
            {
                // Old transcripts feed telemetry only; they must not resurface as chat rows.
                return;
            }

            var s = previous ?? (NewSession(u.SessionId, u.LastActivityAt ?? u.ObservedAt) with { Inferred = true });

            var state = s.State;
            var stateSince = s.StateSince;
            var activityAt = u.LastActivityAt ?? u.ObservedAt;
            if (!s.HookSeen && u.InferredSignal is { } signal && SessionStateMachine.TryNext(state, signal, out var next))
            {
                if (next != state)
                {
                    stateSince = activityAt; // every transcript write re-signals Working; only a real change restarts the timer
                }

                state = next;
            }
            else if (u.Interrupted && state is SessionState.Working or SessionState.Waiting && activityAt >= s.StateSince)
            {
                // Esc ends the turn without a Stop hook; the transcript's interrupt marker is the only evidence there is.
                // An interrupt older than the current state belongs to an earlier turn and must not undo a newer hook.
                state = SessionState.Idle;
                stateSince = activityAt;
            }

            var cwd = s.Cwd ?? u.Cwd;
            // Sub-agent transcripts carry usage for their own model and no Model; the parent's context bar keeps the parent's.
            var model = u.Model ?? s.Model;
            var lastEvent = u.LastActivityAt is { } activity && activity > s.LastEventAt ? activity : s.LastEventAt;
            Commit(previous, s with
            {
                State = state,
                StateSince = stateSince,
                LastEventAt = lastEvent,
                Cwd = cwd,
                WorkspaceId = s.WorkspaceId ?? _resolver.Resolve(cwd),
                TranscriptPath = s.TranscriptPath ?? u.TranscriptPath,
                Model = model,
                Title = s.TitleLocked ? s.Title : u.Title ?? s.Title,
                LatestContext = u.LatestContext ?? s.LatestContext,
                AwaitingToolResult = !u.Interrupted && (u.PendingToolUse ?? s.AwaitingToolResult),
            });
        }
    }

    public void MarkProcessGone(string sessionId) => Signal(sessionId, SessionSignal.ProcessGone);

    public void Rename(string sessionId, string title)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var previous))
            {
                Commit(previous, previous with { Title = title, TitleLocked = true });
            }
        }
    }

    /// <summary>
    /// Timer-driven decay. Idle chats go Stale after <see cref="SessionEngineOptions.StaleAfter"/>. Without hook evidence
    /// "Working" only means "the transcript was written recently", so a quiet inferred Working chat becomes Waiting when
    /// its last assistant message left a tool call unanswered (the spec's permission-prompt case) and Idle otherwise.
    /// Waiting never decays on the idle timer; an inferred Waiting chat with no known claude process gives up after the
    /// stale window, while one with a PID is left to the liveness monitor.
    /// </summary>
    public void SweepStale()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var s in _sessions.Values.ToArray())
            {
                var quiet = now - s.LastEventAt;
                SessionSignal? signal = s.State switch
                {
                    SessionState.Idle when quiet >= _options.StaleAfter => SessionSignal.StaleTimeout,
                    // Only an unknown hook event leaves a session in Starting; nothing else would ever move it.
                    SessionState.Starting when quiet >= _options.InferredIdleAfter => SessionSignal.Stop,
                    SessionState.Working when !s.HookSeen && quiet >= _options.InferredIdleAfter =>
                        s.AwaitingToolResult ? SessionSignal.Notification : SessionSignal.Stop,
                    SessionState.Waiting when !s.HookSeen && s.ClaudePid is null && quiet >= _options.StaleAfter => SessionSignal.Stop,
                    _ => null,
                };

                if (signal is { } decay)
                {
                    SignalLocked(s.SessionId, decay);
                    if (_sessions[s.SessionId].State == SessionState.Idle && quiet >= _options.StaleAfter)
                    {
                        SignalLocked(s.SessionId, SessionSignal.StaleTimeout);
                    }
                }
            }
        }
    }

    public void ReResolveWorkspaces()
    {
        lock (_gate)
        {
            foreach (var s in _sessions.Values.ToArray())
            {
                var resolved = _resolver.Resolve(s.Cwd);
                if (resolved != s.WorkspaceId)
                {
                    Commit(s, s with { WorkspaceId = resolved });
                }
            }
        }
    }

    public void Dispose()
    {
        _sweepTimer?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private bool ProcessStillRuns(int pid, DateTimeOffset seenAt)
    {
        if (_probe is null)
        {
            return true;
        }

        try
        {
            return _probe.IsAlive(pid, seenAt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot query process {Pid} during restore; keeping it", pid);
            return true;
        }
    }

    private void Signal(string sessionId, SessionSignal signal)
    {
        lock (_gate)
        {
            SignalLocked(sessionId, signal);
        }
    }

    private void SignalLocked(string sessionId, SessionSignal signal)
    {
        if (!_sessions.TryGetValue(sessionId, out var previous) || !SessionStateMachine.TryNext(previous.State, signal, out var next))
        {
            return;
        }

        Commit(previous, previous with { State = next, StateSince = _time.GetUtcNow() });
    }

    /// <summary>Stores and publishes a changed snapshot. Must be called under <see cref="_gate"/> so versions and publish order agree.</summary>
    private void Commit(SessionSnapshot? previous, SessionSnapshot candidate)
    {
        if (previous is not null && candidate == previous)
        {
            return;
        }

        var current = candidate with { Version = ++_version };
        _sessions[current.SessionId] = current;
        _bus.Publish(new SessionChanged(previous, current));
    }

    private static SessionSnapshot NewSession(string sessionId, DateTimeOffset at) => new()
    {
        SessionId = sessionId,
        State = SessionState.Starting,
        StartedAt = at,
        LastEventAt = at,
        StateSince = at,
    };

    private static int? PickClaudePid(IReadOnlyList<ProcessRef> chain)
    {
        if (chain.Count == 0)
        {
            return null;
        }

        foreach (var p in chain)
        {
            if (ClaudeNames.Contains(BaseName(p.Name)))
            {
                return p.Pid;
            }
        }

        foreach (var p in chain)
        {
            if (!ShellNames.Contains(BaseName(p.Name)))
            {
                return p.Pid;
            }
        }

        return null;
    }

    private static string BaseName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
}
