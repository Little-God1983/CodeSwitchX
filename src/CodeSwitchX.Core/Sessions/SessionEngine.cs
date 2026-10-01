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

    /// <summary>Per chat, what its agents are up to (see <see cref="Agents"/>); a chat without an entry is treated as before agents were told apart.</summary>
    private readonly Dictionary<string, Agents> _agents = new(StringComparer.Ordinal);

    /// <summary>Chats whose tile the app chose when it started them (<see cref="Claim"/>); their folder does not move them.</summary>
    private readonly Dictionary<string, Guid> _claims = new(StringComparer.Ordinal);
    private const int OpenToolsKept = 32;
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
    /// Loads persisted snapshots. One that comes back as stored is not published (its row is already right); one this
    /// corrects is committed like any other change, so the stored row follows. Hook evidence does not survive a restart
    /// (<see cref="SessionSnapshot.HookSeen"/> resets). A saved claude PID is checked once: if that process is gone the
    /// chat's turn ended while the app was down, so it comes back Idle rather than as a red Errored row, and the PID is
    /// forgotten. A process that started after the chat's last event holds a reused PID and counts as gone.
    /// A Working session that has been quiet longer than the inferred idle window drops to Idle, because its Stop
    /// hook most likely fired while the app was down, and an Idle one quiet for the stale window comes back Stale, as the
    /// first sweep would make it 5 s on (until then its row would sit on the tile). Waiting sessions with a live process
    /// are left alone: a permission prompt is quiet by nature, and the liveness monitor decides when such a chat is really gone.
    /// </summary>
    public void Restore(IEnumerable<SessionSnapshot> persisted)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var snapshot in persisted)
            {
                var restored = snapshot with { HookSeen = false };
                _sessions[restored.SessionId] = restored;

                var corrected = restored;
                if (corrected.ClaudePid is { } pid && !ProcessStillRuns(pid, corrected.LastEventAt))
                {
                    corrected = corrected with { ClaudePid = null };
                    if (corrected.State is SessionState.Working or SessionState.Waiting or SessionState.Starting)
                    {
                        corrected = corrected with { State = SessionState.Idle, StateSince = now };
                    }
                }

                if (corrected.State == SessionState.Working && now - corrected.LastEventAt > _options.InferredIdleAfter)
                {
                    corrected = corrected with { State = SessionState.Idle, StateSince = now };
                }

                if (corrected.State == SessionState.Idle && now - corrected.LastEventAt >= _options.StaleAfter)
                {
                    corrected = corrected with { State = SessionState.Stale, StateSince = StaleSince(corrected) };
                }

                Commit(restored, corrected);
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

    /// <summary>
    /// Puts a chat the app starts itself on the tile of <paramref name="workspaceId"/>, before its first event: a folder
    /// two workspaces share, or one a folder workspace registers too, would otherwise put it on the other tile. Applies to
    /// a chat already known as well. Not stored: the claim lasts as long as this process, as does the chat it started.
    /// </summary>
    public void Claim(string sessionId, Guid workspaceId)
    {
        lock (_gate)
        {
            _claims[sessionId] = workspaceId;
            if (_sessions.GetValueOrDefault(sessionId) is { } s && s.WorkspaceId != workspaceId)
            {
                Commit(s, s with { WorkspaceId = workspaceId });
            }
        }
    }

    public void Apply(HookEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        lock (_gate)
        {
            var previous = _sessions.GetValueOrDefault(e.SessionId);
            var s = previous ?? NewSession(e.SessionId, e.At);

            var state = s.State;
            var signal = SignalOfLocked(e, s);
            // idle_prompt reports a quiet minute. Activity within the quiet window means it raced the next prompt (each relay
            // may take up to a second to land) and describes the turn before, which has already ended.
            var staleIdlePrompt = signal == SessionSignal.IdlePrompt && e.At - s.LastEventAt < _options.InferredIdleAfter;
            if (signal is { } known && !staleIdlePrompt && SessionStateMachine.TryNext(state, known, out var next))
            {
                state = next;
            }
            else if (e.Signal is null && !e.Informational)
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
                WorkspaceId = _claims.TryGetValue(e.SessionId, out var claimed) ? claimed
                    : cwd != s.Cwd || s.WorkspaceId is null ? _resolver.Resolve(cwd) : s.WorkspaceId,
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

    /// <summary>
    /// The signal an event carries once the chat's agents are told apart (<see cref="Agents"/>). Sub-agents run tools while
    /// the parent's turn goes on, and while another agent's permission prompt waits, so the chat waits for a set of agents:
    /// a PermissionRequest adds the agent it names (none: the main agent); a Notification adds the agent it names, or for a
    /// nameless permission_prompt, the agent of the latest tool use still open unless a prompt already waits (it restates
    /// that one), and for any other nameless dialog the main agent. A tool use by a waiting agent takes it out of the set,
    /// and so does its SubagentStop; the Waiting ends when the set is empty, as Working, or as Idle when the main turn ended
    /// meanwhile. A tool use by any other agent leaves the Waiting alone. The main turn's stop or idle prompt takes the main
    /// agent out and ends the Waiting unless a sub-agent still waits; a new prompt takes the main agent out and works on.
    /// Without a set (a restored Waiting, an inferred one) the first tool use ends it, as before.
    /// </summary>
    private SessionSignal? SignalOfLocked(HookEvent e, SessionSnapshot s)
    {
        var agents = _agents.GetValueOrDefault(e.SessionId) ?? new Agents();
        var agent = Agents.Key(e.AgentId);
        var signal = e.Signal;
        switch (e.EventName)
        {
            case "PreToolUse":
                agents.Open.Add((agent, e.ToolUseId));
                if (agents.Open.Count > OpenToolsKept)
                {
                    agents.Open.RemoveAt(0);
                }

                break;
            case "PostToolUse":
                var index = agents.Open.FindLastIndex(t => t.ToolUseId is not null && t.ToolUseId == e.ToolUseId);
                if (index < 0)
                {
                    index = agents.Open.FindLastIndex(t => t.Agent == agent);
                }

                if (index >= 0)
                {
                    agents.Open.RemoveAt(index);
                }

                break;
            case "SubagentStop":
                agents.Open.RemoveAll(t => t.Agent == agent);
                if (agents.Waiting.Remove(agent) && s.State == SessionState.Waiting && agents.Waiting.Count == 0)
                {
                    signal = Released(agents); // the waiting agent stopped (its prompt denied): nobody waits
                }

                break;
        }

        switch (signal)
        {
            case SessionSignal.Notification:
                if (WaitingAgentOf(e, agents) is { } waiting)
                {
                    agents.Waiting.Add(waiting);
                }

                break;
            case SessionSignal.ToolUse when s.State == SessionState.Waiting && agents.Waiting.Count > 0:
                signal = !agents.Waiting.Remove(agent) || agents.Waiting.Count > 0 ? null : Released(agents);
                break;
            case SessionSignal.ToolUse:
                agents.Waiting.Remove(agent);
                break;
            case SessionSignal.Stop or SessionSignal.IdlePrompt:
                agents.Waiting.Remove(Agents.Main);
                agents.Open.RemoveAll(t => t.Agent == Agents.Main);
                if (s.State == SessionState.Waiting && agents.Waiting.Count > 0)
                {
                    agents.TurnEnded = true; // a background sub-agent's prompt still blocks it
                    signal = null;
                }
                else
                {
                    agents.TurnEnded = false;
                }

                break;
            case SessionSignal.PromptSubmit:
                agents.Waiting.Remove(Agents.Main);
                agents.Open.RemoveAll(t => t.Agent == Agents.Main);
                agents.TurnEnded = false;
                break;
            case SessionSignal.SessionStart or SessionSignal.SessionEnd:
                agents = new Agents();
                break;
        }

        if (agents.Idle)
        {
            _agents.Remove(e.SessionId);
        }
        else
        {
            _agents[e.SessionId] = agents;
        }

        return signal;
    }

    /// <summary>The agent a prompt waits for, or null for a permission_prompt that restates a prompt already waited for.</summary>
    private static string? WaitingAgentOf(HookEvent e, Agents agents)
    {
        if (e.EventName == "PermissionRequest" || e.AgentId is not null)
        {
            return Agents.Key(e.AgentId);
        }

        if (!string.Equals(e.NotificationType, "permission_prompt", StringComparison.OrdinalIgnoreCase))
        {
            return Agents.Main; // a dialog in the chat itself: the main agent's next own event ends it
        }

        // The Notification names no agent and comes 6 s after the prompt opened: the prompt belongs to a tool use still open.
        return agents.Waiting.Count > 0 ? null : agents.Open.Count > 0 ? agents.Open[^1].Agent : Agents.Main;
    }

    /// <summary>Nobody waits any more: the parent's turn goes on, or is over when it ended while the prompt was open.</summary>
    private static SessionSignal Released(Agents agents)
    {
        var ended = agents.TurnEnded;
        agents.TurnEnded = false;
        return ended ? SessionSignal.Stop : SessionSignal.ToolUse;
    }

    /// <summary>
    /// What a chat's agents are up to, kept only while a chat has hooks in this run: the tool uses whose PreToolUse has no
    /// PostToolUse yet, the agents whose prompt waits for the user, and whether the main turn ended meanwhile. Not persisted.
    /// </summary>
    private sealed class Agents
    {
        /// <summary>The main agent's key; sub-agents are keyed by their agent_id.</summary>
        public const string Main = "";

        public List<(string Agent, string? ToolUseId)> Open { get; } = [];

        public HashSet<string> Waiting { get; } = new(StringComparer.Ordinal);

        public bool TurnEnded { get; set; }

        public bool Idle => Open.Count == 0 && Waiting.Count == 0 && !TurnEnded;

        public static string Key(string? agentId) => agentId ?? Main;
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
            // Esc ends the turn without a Stop hook; the transcript's interrupt marker is the only evidence there is, and the
            // newest: Interrupted means the file ends with it, so the same lines infer nothing newer (their recent writes
            // would say Working). For a hook-backed chat an interrupt older than the current state belongs to an earlier
            // turn and must not undo a newer hook; without hooks the state came from this transcript or a sweep of it.
            if (u.Interrupted && state is SessionState.Working or SessionState.Waiting or SessionState.Starting && (!s.HookSeen || activityAt >= s.StateSince))
            {
                state = SessionState.Idle;
                stateSince = activityAt;
            }
            else if (!u.Interrupted && !s.HookSeen && u.InferredSignal is { } signal && SessionStateMachine.TryNext(state, signal, out var next))
            {
                if (next != state)
                {
                    stateSince = activityAt; // every transcript write re-signals Working; only a real change restarts the timer
                }

                state = next;
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
                WorkspaceId = _claims.TryGetValue(s.SessionId, out var claimed) ? claimed : s.WorkspaceId ?? _resolver.Resolve(cwd),
                TranscriptPath = s.TranscriptPath ?? u.TranscriptPath,
                Model = model,
                Title = s.TitleLocked ? s.Title : u.Title ?? s.Title,
                LatestContext = u.LatestContext ?? s.LatestContext,
                AwaitingToolResult = !u.Interrupted && (u.PendingToolUse ?? s.AwaitingToolResult),
            });
        }
    }

    /// <summary>
    /// Errored, when the chat's claude is still <paramref name="pid"/> as of the event the probe was made for
    /// (<paramref name="seenAt"/>, its <see cref="SessionSnapshot.LastEventAt"/> then). A chat that moved on while it
    /// was probed is left alone: a "claude --resume" that got the same PID back was never probed. Returns whether it applied.
    /// </summary>
    public bool MarkProcessGone(string sessionId, int pid, DateTimeOffset seenAt)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var s) && s.ClaudePid == pid && s.LastEventAt == seenAt)
            {
                SignalLocked(sessionId, SessionSignal.ProcessGone);
                return true;
            }

            return false;
        }
    }

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
                    SignalLocked(s.SessionId, decay, decay == SessionSignal.StaleTimeout ? StaleSince(s) : null);
                    if (_sessions[s.SessionId].State == SessionState.Idle && quiet >= _options.StaleAfter)
                    {
                        SignalLocked(s.SessionId, SessionSignal.StaleTimeout, StaleSince(s));
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
                var resolved = _claims.TryGetValue(s.SessionId, out var claimed) ? claimed : _resolver.Resolve(s.Cwd);
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

    /// <summary>
    /// When a chat quiet for the whole stale window became Stale: the end of that window, not the sweep or restore that
    /// noticed. The tile keeps a Stale row 30 min from here, so yesterday's chats must not date from this morning's start.
    /// </summary>
    private DateTimeOffset StaleSince(SessionSnapshot s) => s.LastEventAt + _options.StaleAfter;

    /// <param name="since">When the new state began, where the caller knows better than now (a chat found stale).</param>
    private void SignalLocked(string sessionId, SessionSignal signal, DateTimeOffset? since = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var previous) || !SessionStateMachine.TryNext(previous.State, signal, out var next))
        {
            return;
        }

        Commit(previous, previous with { State = next, StateSince = since ?? _time.GetUtcNow() });
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
