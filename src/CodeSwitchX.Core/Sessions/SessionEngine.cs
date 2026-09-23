using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>Reconciles hook events, transcript facts and liveness into one <see cref="SessionSnapshot"/> per chat.</summary>
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
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private ITimer? _sweepTimer;

    public SessionEngine(IEventBus bus, IWorkspaceResolver resolver, TimeProvider time, ILogger<SessionEngine> logger,
        SessionEngineOptions? options = null)
    {
        _bus = bus;
        _resolver = resolver;
        _time = time;
        _logger = logger;
        _options = options ?? new SessionEngineOptions();
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

    public void Restore(IEnumerable<SessionSnapshot> persisted)
    {
        lock (_gate)
        {
            foreach (var snapshot in persisted)
            {
                _sessions[snapshot.SessionId] = snapshot;
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
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            previous = _sessions.GetValueOrDefault(e.SessionId);
            var s = previous ?? NewSession(e.SessionId, e.At);

            var state = s.State;
            if (e.Signal is { } signal && SessionStateMachine.TryNext(state, signal, out var next))
            {
                state = next;
            }
            else if (e.Signal is null)
            {
                _logger.LogInformation("Unknown hook event {EventName} for session {SessionId}", e.EventName, e.SessionId);
            }

            var cwd = e.Cwd ?? s.Cwd;
            current = s with
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
                ClaudePid = PickClaudePid(e.ParentChain) ?? s.ClaudePid,
            };
            _sessions[e.SessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    public void Apply(TranscriptUpdate u)
    {
        ArgumentNullException.ThrowIfNull(u);
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            previous = _sessions.GetValueOrDefault(u.SessionId);
            var s = previous ?? (NewSession(u.SessionId, u.LastActivityAt ?? u.ObservedAt) with { Inferred = true });

            var state = s.State;
            var stateSince = s.StateSince;
            if (s.Inferred && u.InferredSignal is { } signal && SessionStateMachine.TryNext(state, signal, out var next))
            {
                state = next;
                stateSince = u.LastActivityAt ?? u.ObservedAt;
            }

            var cwd = s.Cwd ?? u.Cwd;
            var model = u.Usage.Count > 0 ? u.Usage[^1].Model : u.Model ?? s.Model;
            var lastEvent = u.LastActivityAt is { } activity && activity > s.LastEventAt ? activity : s.LastEventAt;
            current = s with
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
            };
            _sessions[u.SessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    public void MarkProcessGone(string sessionId) => Signal(sessionId, SessionSignal.ProcessGone);

    public void Rename(string sessionId, string title)
    {
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out previous))
            {
                return;
            }

            current = previous with { Title = title, TitleLocked = true };
            _sessions[sessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    public void SweepStale()
    {
        var now = _time.GetUtcNow();
        List<string> stale;
        lock (_gate)
        {
            stale = _sessions.Values
                .Where(s => s.State == SessionState.Idle && now - s.LastEventAt >= _options.StaleAfter)
                .Select(s => s.SessionId)
                .ToList();
        }

        foreach (var id in stale)
        {
            Signal(id, SessionSignal.StaleTimeout);
        }
    }

    public void ReResolveWorkspaces()
    {
        var changes = new List<(SessionSnapshot Previous, SessionSnapshot Current)>();
        lock (_gate)
        {
            foreach (var (id, s) in _sessions.ToArray())
            {
                var resolved = _resolver.Resolve(s.Cwd);
                if (resolved != s.WorkspaceId)
                {
                    var updated = s with { WorkspaceId = resolved };
                    _sessions[id] = updated;
                    changes.Add((s, updated));
                }
            }
        }

        foreach (var (previous, current) in changes)
        {
            _bus.Publish(new SessionChanged(previous, current));
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

    private void Signal(string sessionId, SessionSignal signal)
    {
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out previous))
            {
                return;
            }

            if (!SessionStateMachine.TryNext(previous.State, signal, out var next))
            {
                return;
            }

            current = previous with { State = next, StateSince = _time.GetUtcNow() };
            _sessions[sessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    private static SessionSnapshot NewSession(string sessionId, DateTimeOffset at) => new()
    {
        SessionId = sessionId,
        State = SessionState.Starting,
        StartedAt = at,
        LastEventAt = at,
        StateSince = at,
    };

    private void PublishIfChanged(SessionSnapshot? previous, SessionSnapshot current)
    {
        if (previous is null || previous != current)
        {
            _bus.Publish(new SessionChanged(previous, current));
        }
    }

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
