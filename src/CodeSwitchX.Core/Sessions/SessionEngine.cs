using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, Guid> _claims = new(StringComparer.Ordinal);

    /// <summary>Per chat, the claude process whose VS Code window was looked up (<see cref="LookUp"/>); not stored.</summary>
    private readonly ConcurrentDictionary<string, int> _windowLookups = new(StringComparer.Ordinal);

    /// <summary>Per chat, the claude process whose window lookup could not tell yet, and how often (<see cref="MaxWindowTries"/>).</summary>
    private readonly ConcurrentDictionary<string, (int Pid, int Tries)> _windowMisses = new(StringComparer.Ordinal);

    /// <summary>How often a lookup that cannot tell yet is tried for one claude before the chat keeps what it has.</summary>
    internal const int MaxWindowTries = 5;
    private readonly IIdeWindows? _windows;
    private const int OpenToolsKept = 32;
    private readonly List<IDisposable> _subscriptions = [];
    private ITimer? _sweepTimer;
    private long _version;

    public SessionEngine(IEventBus bus, IWorkspaceResolver resolver, TimeProvider time, ILogger<SessionEngine> logger,
        SessionEngineOptions? options = null, IProcessProbe? probe = null, IIdeWindows? windows = null)
    {
        _bus = bus;
        _resolver = resolver;
        _time = time;
        _logger = logger;
        _options = options ?? new SessionEngineOptions();
        _probe = probe;
        _windows = windows;
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
    /// The wait for a turn that reacts to the end of the last job (<see cref="SessionSnapshot.JobsEndedAt"/>) is kept only by a
    /// chat that comes back Waiting with no job open (#395), for when its prompt is answered.
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

                // A title stored before #118 can be the start of a cross-session envelope, which names no task: none then. No cut:
                // a title is never shortened here, whatever length it was stored at.
                var corrected = restored.TitleLocked ? restored : restored with { Title = ChatTitle.FromPrompt(restored.Title, int.MaxValue) };
                if (corrected.ClaudePid is { } pid && !ProcessStillRuns(pid, corrected.LastEventAt))
                {
                    corrected = corrected with { ClaudePid = null };
                    if (corrected.State is SessionState.Working or SessionState.Waiting or SessionState.Starting)
                    {
                        corrected = corrected with { State = SessionState.Idle, StateSince = now };
                    }
                }

                // With no process to watch, nothing would ever clear a job with no end marker.
                if (corrected.ClaudePid is null && corrected.OpenJobs.Count > 0)
                {
                    corrected = corrected with { OpenJobs = [], TurnOverAt = null };
                }

                // A chat still running a foreground command or holding a job has said all it will say until that is over. Any
                // other quiet Working chat may have lost its Stop while the app was closed, and no later line would tell.
                var runsTool = corrected.AwaitingToolResult && corrected.ClaudePid is not null;
                if (corrected.State == SessionState.Working && now - corrected.LastEventAt > _options.InferredIdleAfter && !runsTool && corrected.OpenJobs.Count == 0)
                {
                    corrected = corrected with { State = SessionState.Idle, StateSince = now };
                }

                if (corrected.State == SessionState.Working && runsTool)
                {
                    corrected = corrected with { RestoredWorking = true };
                }

                if (corrected.State == SessionState.Idle && now - corrected.LastEventAt >= _options.StaleAfter)
                {
                    corrected = corrected with { State = SessionState.Stale, StateSince = StaleSince(corrected) };
                }

                // The wait for a turn that reacts to the end of the last job is stored (#395): a chat restored Waiting on a
                // background agent's prompt keeps it for when the prompt is answered. Any other restores as before: a Working
                // one's wait may have run out while the app was down, and the first sweep would end it before its transcript
                // tells of a reacting turn that began meanwhile.
                var keepsWait = corrected.State == SessionState.Waiting && corrected.OpenJobs.Count == 0;
                corrected = corrected with { JobsEndedAt = keepsWait ? corrected.JobsEndedAt : null };
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
    /// a chat already known as well. Not stored: the claim lasts as long as this process; after a restart the chat's VS
    /// Code window places it.
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

    /// <summary>
    /// How long the end of a chat's last job may go without a sign of the turn it starts before the chat counts as idle: most
    /// such ends have a turn that reacts, but some have none (a quarter had no Stop within the hour).
    /// </summary>
    public static readonly TimeSpan JobEndSettle = TimeSpan.FromMinutes(2);

    /// <summary>The jobs each chat's hooks or transcript told the end of, since its process started: a start told after one opens nothing. Under <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, RecentIds> _endedJobs = new(StringComparer.Ordinal);

    /// <summary>Per chat, what tells a main step's hook told late (<see cref="MainTurn"/>). Under <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, MainTurn> _mainTurns = new(StringComparer.Ordinal);

    public void Apply(HookEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var chainPid = PickClaudePid(e.ParentChain);
        var lookup = LookUp(e.SessionId, chainPid, e.ParentChain, seenAt: null);
        lock (_gate)
        {
            var previous = _sessions.GetValueOrDefault(e.SessionId);
            var s = previous ?? NewSession(e.SessionId, e.At);

            var state = s.State;
            var signal = SignalOfLocked(e, s, out var late);
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

            var jobs = JobsAfterLocked(e, signal, s);
            // A stale idle prompt describes the turn before: it tells nothing new of the main agent's turn.
            // Nor does a hook of the main agent's step told late (#396, #397).
            var ofMainTurn = !staleIdlePrompt && !late.Any && OfMainTurnLocked(e, signal);
            var turnOver = staleIdlePrompt ? s.TurnOverAt : TurnOverAfter(e, signal, s, jobs, ofMainTurn);
            // What tells nothing of the main agent's turn (a background agent's steps and prompts) leaves a settling end as it is.
            var jobsEndedAt = ofMainTurn ? null : s.JobsEndedAt;
            if (late.StoppedBefore is { } stoppedAt && jobs.Count > s.OpenJobs.Count)
            {
                // A job start told after the turn's Stop (#397): the turn was over when the Stop came, before the job was told.
                turnOver ??= stoppedAt;
            }

            if (state == SessionState.Idle && jobs.Count > 0)
            {
                state = SessionState.Working;
            }
            else if (e.EndedJob is not null && TurnOverOfLastJobEnd(jobs, s, state) is not null)
            {
                turnOver = null;
                if (state == SessionState.Working)
                {
                    // A sub-agent stopped the last job while the chat sat at its input prompt, or the stop's hook landed after
                    // the turn's Stop (#381). No turn reacts to a stop, which tells no task notification (#379): the chat is done.
                    state = SessionState.Idle;
                }
                else
                {
                    // Stopped while a background agent's prompt holds the chat Waiting (#390): once that prompt is answered, the
                    // wait for a reacting turn runs out as for any end after the turn.
                    jobsEndedAt = e.At;
                }
            }

            var cwd = e.Cwd ?? s.Cwd;
            var ping = e.EventName == "UserPromptSubmit" && KeepAlive.IsPing(e.Prompt);
            var claudePid = chainPid ?? s.ClaudePid;
            var window = WindowOfLocked(s, claudePid, lookup);
            var windowChanged = !SameFolders(window, s.WindowFolders);
            var awaitingToolResult = e.EventName switch
            {
                "PreToolUse" when late.StaleStart => s.AwaitingToolResult, // its result came already
                "PreToolUse" or "PermissionRequest" => true,
                "PostToolUse" => false,
                _ => s.AwaitingToolResult,
            };
            Commit(previous, s with
            {
                State = state,
                StateSince = state == s.State && previous is not null ? s.StateSince : e.At,
                LastEventAt = e.At > s.LastEventAt ? e.At : s.LastEventAt,
                // The event's own signal: a tool use the agents make no progress of is still the chat at work.
                // A keep-alive ping is the app's own, not the user's (#386).
                LastUseAt = ping ? s.LastUseAt : UsedAt(s, e.Signal, state, e.At),
                Cwd = cwd,
                WorkspaceId = _claims.TryGetValue(e.SessionId, out var claimed) ? claimed
                    : cwd != s.Cwd || s.WorkspaceId is null || windowChanged ? _resolver.Resolve(cwd, window) : s.WorkspaceId,
                WindowFolders = windowChanged ? window : s.WindowFolders,
                TranscriptPath = e.TranscriptPath ?? s.TranscriptPath,
                Model = e.Model ?? s.Model,
                LastToolName = e.ToolName ?? s.LastToolName,
                LastNotification = e.Signal == SessionSignal.Notification ? e.Message ?? e.NotificationType : s.LastNotification,
                Title = s.Title ?? (ping ? null : ChatTitle.FromPrompt(e.Prompt, _options.TitleMaxLength)),
                Inferred = false,
                HookSeen = true,
                AwaitingToolResult = awaitingToolResult,
                ClaudePid = claudePid,
                TurnFailed = e.EventName switch
                {
                    "StopFailure" => true,
                    "Stop" or "UserPromptSubmit" or "SessionStart" => false,
                    _ => s.TurnFailed,
                },
                TurnStartedAt = e.EventName == "UserPromptSubmit" ? e.At : s.TurnStartedAt,
                KeepAliveTurn = e is { EventName: "UserPromptSubmit", AgentId: null } ? ping : KeepAliveGoesOn(s),
                OpenJobs = jobs,
                TurnOverAt = turnOver,
                JobsEndedAt = WaitAfter(s.State, state, jobs, jobsEndedAt, e.At),
            });
        }
    }

    /// <summary>
    /// <see cref="SessionSnapshot.JobsEndedAt"/> once a chat in <paramref name="was"/> moves to <paramref name="state"/> at
    /// <paramref name="at"/>: Waiting to Working is the prompt answered, and a turn that reacts to the end may have waited
    /// behind it, so the wait for it begins at <paramref name="at"/>. On the transcript route too, which tells it for a chat
    /// restored Waiting before its first hook: its stored wait must not run out at once (#395). The caller weighs a reacting
    /// request against the wait first, as the answer must not put the wait past it.
    /// </summary>
    private static DateTimeOffset? WaitAfter(SessionState was, SessionState state, IReadOnlyList<string> jobs, DateTimeOffset? endedAt, DateTimeOffset at) =>
        WaitAfter(state, jobs, was == SessionState.Waiting && state == SessionState.Working && endedAt is not null ? at : endedAt);

    /// <summary>
    /// <see cref="SessionSnapshot.JobsEndedAt"/> as a chat in <paramref name="state"/> with <paramref name="jobs"/> open keeps
    /// it: the wait for the turn that reacts to the end of its last job holds only while it works on, or waits, with no job
    /// open. A job open again is waited for by itself, and a chat that is done waits for nothing (#390).
    /// </summary>
    private static DateTimeOffset? WaitAfter(SessionState state, IReadOnlyList<string> jobs, DateTimeOffset? endedAt) =>
        jobs.Count == 0 && state is SessionState.Working or SessionState.Waiting ? endedAt : null;

    /// <summary>
    /// The chat's open jobs once this event is applied (#332). A new process took the old ones with it, and so did the chat's
    /// end; a compact, which has no signal, leaves them. A sub-agent's job is left out: its end may never reach the chat's
    /// own transcript. A stopped job ends here (#381), before the turn's Stop, which follows sooner than the transcript is
    /// read; from a sub-agent too, as the transcript counts its stops. The same list when nothing changed, as a snapshot
    /// compares by it.
    /// </summary>
    private IReadOnlyList<string> JobsAfterLocked(HookEvent e, SessionSignal? signal, SessionSnapshot s)
    {
        if (signal is SessionSignal.SessionStart or SessionSignal.SessionEnd)
        {
            _endedJobs.Remove(e.SessionId);
            return s.OpenJobs.Count > 0 ? [] : s.OpenJobs;
        }

        var jobs = e.BackgroundJob is { } job && e.AgentId is null ? WithJob(s.OpenJobs, e.SessionId, job) : s.OpenJobs;
        return e.EndedJob is { } ended ? WithoutJobLocked(jobs, e.SessionId, ended) : jobs;
    }

    private IReadOnlyList<string> WithJob(IReadOnlyList<string> jobs, string sessionId, string job) =>
        jobs.Contains(job) || (_endedJobs.TryGetValue(sessionId, out var ended) && ended.Contains(job)) ? jobs : [.. jobs, job];

    /// <summary>
    /// When the chat's turn was over, if its last job just ended after that, while it sat at its input prompt or while a
    /// background agent's prompt held it Waiting (#390); else null.
    /// </summary>
    private static DateTimeOffset? TurnOverOfLastJobEnd(IReadOnlyList<string> jobs, SessionSnapshot s, SessionState state) =>
        jobs.Count == 0 && s.OpenJobs.Count > 0 && state is SessionState.Working or SessionState.Waiting ? s.TurnOverAt : null;

    /// <summary>
    /// Whether an event tells of the main agent's turn (#390): an event with no signal does not, nor does any of a background
    /// agent's, its prompts included. A prompt whose hook names no agent is the main agent's only when the main agent is the
    /// one it waits for (<see cref="Agents"/>); one that restates a background agent's prompt is that agent's. The start of
    /// the main agent's step is its turn even where it is no progress for a Waiting another agent's prompt holds; a step's
    /// result is not, as it may land after the turn's Stop.
    /// </summary>
    private bool OfMainTurnLocked(HookEvent e, SessionSignal? signal) => (signal ?? (MainStepBegins(e) ? e.Signal : null)) switch
    {
        null => false,
        _ when e.AgentId is not null => false,
        SessionSignal.Notification => _agents.TryGetValue(e.SessionId, out var agents) && agents.Waiting.Contains(Agents.Main),
        _ => true,
    };

    /// <summary>The jobs without one that ended, remembered as ended so a start told later opens nothing.</summary>
    private IReadOnlyList<string> WithoutJobLocked(IReadOnlyList<string> jobs, string sessionId, string job)
    {
        if (!_endedJobs.TryGetValue(sessionId, out var known))
        {
            _endedJobs[sessionId] = known = new RecentIds(RecentIds.JobsKept);
        }

        known.Add(job);
        return jobs.Contains(job) ? jobs.Where(j => j != job).ToArray() : jobs;
    }

    /// <summary>
    /// The newest ids of a kind a chat is done with: the ends of its jobs (#390), the main steps whose result came (#396,
    /// #397). What is told late comes seconds after, by another route or relay, so the newest ones are enough, and a chat that
    /// runs for days does not keep every id it ever saw.
    /// </summary>
    internal sealed class RecentIds(int kept)
    {
        internal const int JobsKept = 1024;

        internal const int StepsKept = 64;

        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public bool Contains(string id) => _ids.Contains(id);

        public void Add(string id)
        {
            if (!_ids.Add(id))
            {
                return;
            }

            _order.Enqueue(id);
            if (_order.Count > kept)
            {
                _ids.Remove(_order.Dequeue());
            }
        }
    }

    /// <summary>
    /// What tells a hook of the main agent's own steps told late (#396, #397), each by a relay of its own that may land up to
    /// a second after the next: when its turn stopped, until its next prompt or step, and the steps whose result came. Claude
    /// Code ends every step of a turn before its Stop, so a step result after the Stop was told late; and a step start after
    /// its own result was too. Not known after a restart, when every hook counts as before.
    /// </summary>
    private sealed class MainTurn
    {
        public DateTimeOffset? StoppedAt { get; set; }

        public RecentIds Done { get; } = new(RecentIds.StepsKept);
    }

    /// <summary>What a hook of the main agent's own step told late is (<see cref="MainTurn"/>).</summary>
    private readonly record struct ToldLate(bool StaleStart, DateTimeOffset? StoppedBefore)
    {
        public bool Any => StaleStart || StoppedBefore is not null;
    }

    /// <summary>
    /// <see cref="SessionSnapshot.TurnOverAt"/> once this event is applied: a stop, or the idle prompt notice, with jobs open
    /// ends the turn; a prompt, a question or a step of the main agent is a turn again. A background agent's steps say nothing
    /// about the chat's own turn. Nor does a stop's result (#381): it ends a step its PreToolUse already told, and one that
    /// lands after the turn's Stop must not open the turn again. Nor does a background agent's prompt (#390).
    /// </summary>
    private static DateTimeOffset? TurnOverAfter(HookEvent e, SessionSignal? signal, SessionSnapshot s, IReadOnlyList<string> jobs, bool ofMainTurn) =>
        // The main turn's own stop counts though a background agent's prompt holds the Waiting on (#390).
        (signal ?? (e.AgentId is null && e.Signal is SessionSignal.Stop or SessionSignal.IdlePrompt ? e.Signal : null)) switch
    {
        SessionSignal.Stop => jobs.Count > 0 ? e.At : null,
        SessionSignal.IdlePrompt => jobs.Count > 0 ? s.TurnOverAt ?? e.At : null,
        _ when !ofMainTurn || e.EndedJob is not null => s.TurnOverAt,
        _ => null,
    };

    /// <summary>
    /// The signal an event carries once the chat's agents are told apart (<see cref="Agents"/>). Sub-agents run tools while
    /// the parent's turn goes on, and while another agent's permission prompt waits, so the chat waits for a set of agents:
    /// a PermissionRequest adds the agent it names (none: the main agent); a Notification adds the agent it names, or for a
    /// nameless permission_prompt, the agent of the latest tool use still open unless a prompt already waits (it restates
    /// that one), and for any other nameless dialog the main agent. A tool use by a waiting agent takes it out of the set,
    /// and so does its SubagentStop; the Waiting ends when the set is empty, as Working, or as Idle when the main turn ended
    /// meanwhile. A tool use by any other agent leaves the Waiting alone. The main turn's stop or idle prompt takes the main
    /// agent out and ends the Waiting unless a sub-agent still waits; a new prompt takes the main agent out and works on.
    /// The PreToolUse of the very step a prompt waits for, when the prompt names it and it comes late, is no progress.
    /// Without a set (a restored Waiting, an inferred one) the first tool use ends it, as before.
    /// </summary>
    /// <param name="late">Whether the event is a hook of the main agent's step told late (<see cref="MainTurn"/>): no progress.</param>
    private SessionSignal? SignalOfLocked(HookEvent e, SessionSnapshot s, out ToldLate late)
    {
        late = MainStepToldLateLocked(e);
        if (late.Any)
        {
            // It changes nothing of what the agents are up to: the step's result came already, or its turn stopped.
            return null;
        }

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
                    signal = Released(agents, s); // the waiting agent stopped (its prompt denied): nobody waits
                }

                break;
        }

        switch (signal)
        {
            case SessionSignal.Notification:
                if (WaitingAgentOf(e, agents) is { } waiting)
                {
                    agents.Waiting.Add(waiting);
                    if (e.ToolUseId is { } asking)
                    {
                        agents.WaitingSteps.Add(asking);
                    }
                }

                break;
            case SessionSignal.ToolUse when s.State == SessionState.Waiting && e.EventName == "PreToolUse" && e.ToolUseId is { } step
                && agents.WaitingSteps.Contains(step):
                // The start of the very step that waits, told late (its relay and the one that asks run side by side): no progress.
                signal = null;
                break;
            case SessionSignal.ToolUse when s.State == SessionState.Waiting && agents.Waiting.Count > 0:
                if (MainStepBegins(e))
                {
                    agents.TurnEnded = false; // the main agent steps again (#390): its turn goes on once the prompts are answered
                }

                signal = !agents.Waiting.Remove(agent) || agents.Waiting.Count > 0 ? null : Released(agents, s);
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

        if (agents.Waiting.Count == 0)
        {
            agents.WaitingSteps.Clear();
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

    /// <summary>
    /// Keeps the chat's <see cref="MainTurn"/> up to date with the event and tells whether it is a hook of the main agent's
    /// step told late (#396, #397): a step start whose result came already, or a step result after the turn's Stop with no
    /// prompt or step of its own since. A job start or end such a result tells still counts; it is no progress otherwise.
    /// </summary>
    private ToldLate MainStepToldLateLocked(HookEvent e)
    {
        if (e.EventName is "SessionStart" or "SessionEnd")
        {
            _mainTurns.Remove(e.SessionId);
            return default;
        }

        if (e.AgentId is not null || e.EventName is not ("Stop" or "StopFailure" or "UserPromptSubmit" or "PreToolUse" or "PostToolUse"))
        {
            return default;
        }

        if (!_mainTurns.TryGetValue(e.SessionId, out var turn))
        {
            _mainTurns[e.SessionId] = turn = new MainTurn();
        }

        switch (e.EventName)
        {
            case "Stop" or "StopFailure":
                turn.StoppedAt = e.At;
                return default;
            case "UserPromptSubmit":
                turn.StoppedAt = null;
                return default;
            case "PreToolUse" when e.ToolUseId is { } step && turn.Done.Contains(step):
                return new ToldLate(StaleStart: true, StoppedBefore: null);
            case "PreToolUse":
                turn.StoppedAt = null; // the main agent steps again: a turn that reacts to a job's end needs no prompt
                return default;
            default: // PostToolUse
                if (e.ToolUseId is { } done)
                {
                    turn.Done.Add(done);
                }

                return turn.StoppedAt is { } stopped ? new ToldLate(StaleStart: false, StoppedBefore: stopped) : default;
        }
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

    /// <summary>
    /// Nobody waits any more: the parent's turn goes on, or is over when it ended while the prompt was open. Unless its last
    /// job ended since (#390): the turn that reacts may still come, and the chat works on until the wait for it runs out.
    /// </summary>
    private static SessionSignal Released(Agents agents, SessionSnapshot s)
    {
        var ended = agents.TurnEnded;
        agents.TurnEnded = false;
        return ended && s.JobsEndedAt is null ? SessionSignal.Stop : SessionSignal.ToolUse;
    }

    /// <summary>Whether the event starts a step of the main agent: a step's result may land after the turn's Stop (#381).</summary>
    private static bool MainStepBegins(HookEvent e) => e is { EventName: "PreToolUse", AgentId: null };

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

        /// <summary>The tool uses whose prompt waits, where the prompt named one: their own late PreToolUse is no progress.</summary>
        public HashSet<string> WaitingSteps { get; } = new(StringComparer.Ordinal);

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

            var jobs = JobsAfterLocked(u, s);
            var turnOver = s.TurnOverAt;
            var jobsEndedAt = s.JobsEndedAt;

            // The turn is over, as Esc or the transcript's quiet tells it, but a job goes on.
            if (state == SessionState.Idle && jobs.Count > 0)
            {
                stateSince = s.State == SessionState.Working ? s.StateSince : activityAt;
                state = SessionState.Working;
                turnOver ??= activityAt;
            }
            else if (TurnOverOfLastJobEnd(jobs, s, state) is { } over)
            {
                // The last job ended. A stop since then was the turn that reacted to it, read late: the chat is done. Else that
                // turn may still come, and the sweep ends the wait if it does not; while a background agent's prompt holds the
                // chat Waiting (#390), it begins again once the prompt is answered.
                turnOver = null;
                var endedAt = u.JobsEnded.Max(j => j.At);
                if (endedAt <= over && state == SessionState.Working)
                {
                    state = SessionState.Idle;
                    stateSince = activityAt;
                }
                else
                {
                    jobsEndedAt = endedAt;
                }
            }

            // A request of the chat's own (a sub-agent's lines hold none) after the end is the turn that reacts to it: it ends
            // with its own stop, so the wait for it is over (#390).
            if (jobsEndedAt is { } settling && u.LastRequestAt is { } reacting && reacting > settling)
            {
                jobsEndedAt = null;
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
                WorkspaceId = _claims.TryGetValue(s.SessionId, out var claimed) ? claimed : s.WorkspaceId ?? _resolver.Resolve(cwd, s.WindowFolders),
                TranscriptPath = s.TranscriptPath ?? u.TranscriptPath,
                Model = model,
                Title = s.TitleLocked ? s.Title : u.Title ?? s.Title,
                LatestContext = u.LatestContext ?? s.LatestContext,
                LastRequestAt = u.LastRequestAt is { } request && (s.LastRequestAt is not { } known || request > known) ? request : s.LastRequestAt,
                AwaitingToolResult = !u.Interrupted && (u.PendingToolUse ?? s.AwaitingToolResult),
                OpenJobs = jobs,
                TurnOverAt = turnOver,
                JobsEndedAt = WaitAfter(s.State, state, jobs, jobsEndedAt, activityAt),
                RestoredWorking = s.RestoredWorking && u.PendingToolUse != false,
                KeepAliveTurn = KeepAliveGoesOn(s),
            });
        }
    }

    /// <summary>
    /// Whether the chat's keep-alive turn (#386) is still what the next change is about: until the user's next prompt, so the
    /// Yard shows the chat as idle since the user left it; but not once the turn is over with a background job open, whose
    /// end is the user's own news again.
    /// </summary>
    private static bool KeepAliveGoesOn(SessionSnapshot s) => s.KeepAliveTurn && s.TurnOverAt is null;

    /// <summary>
    /// The chat's open jobs once these lines are applied. A start counts only for a chat whose end or process can clear it, in
    /// lines that are not old, and from since the chat began: a copy <c>claude --resume</c> made keeps its old time.
    /// </summary>
    private IReadOnlyList<string> JobsAfterLocked(TranscriptUpdate u, SessionSnapshot s)
    {
        var jobs = s.OpenJobs;
        if (!u.Historical && (s.HookSeen || s.ClaudePid is not null))
        {
            foreach (var started in u.JobsStarted.Where(j => j.At >= s.StartedAt))
            {
                jobs = WithJob(jobs, s.SessionId, started.Id);
            }
        }

        foreach (var ended in u.JobsEnded)
        {
            jobs = WithoutJobLocked(jobs, s.SessionId, ended.Id);
        }

        return jobs;
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
                    // From the end itself (#390): a background agent's steps, which keep the wait, must not put it off either.
                    SessionState.Working when s.JobsEndedAt is { } ended && now - ended >= JobEndSettle => SessionSignal.Stop,
                    SessionState.Working when !s.HookSeen && !s.RestoredWorking && s.OpenJobs.Count == 0 && quiet >= _options.InferredIdleAfter =>
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
        SessionSnapshot[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
        }

        var lookups = new Dictionary<string, WindowLookup>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            if (LookUp(s.SessionId, s.ClaudePid, chain: null, seenAt: s.LastEventAt) is { } lookup)
            {
                lookups[s.SessionId] = lookup;
            }
        }

        lock (_gate)
        {
            foreach (var s in _sessions.Values.ToArray())
            {
                var window = WindowOfLocked(s, s.ClaudePid, lookups.GetValueOrDefault(s.SessionId));
                var windowChanged = !SameFolders(window, s.WindowFolders);
                var resolved = _claims.TryGetValue(s.SessionId, out var claimed) ? claimed : _resolver.Resolve(s.Cwd, window);
                if (resolved != s.WorkspaceId || windowChanged)
                {
                    Commit(s, s with { WorkspaceId = resolved, WindowFolders = windowChanged ? window : s.WindowFolders });
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

        var over = next is SessionState.Ended or SessionState.Errored;
        if (over)
        {
            _endedJobs.Remove(sessionId);
            _mainTurns.Remove(sessionId); // a chat whose process went gets no SessionEnd to clear it
        }

        Commit(previous, previous with
        {
            State = next,
            StateSince = since ?? _time.GetUtcNow(),
            OpenJobs = over && previous.OpenJobs.Count > 0 ? [] : previous.OpenJobs,
            TurnOverAt = over ? null : previous.TurnOverAt,
            JobsEndedAt = next == SessionState.Idle || over ? null : previous.JobsEndedAt,
            KeepAliveTurn = KeepAliveGoesOn(previous),
        });
    }

    /// <summary>
    /// <see cref="SessionSnapshot.LastUseAt"/> once <paramref name="signal"/> of the chat <paramref name="s"/> is applied at
    /// <paramref name="at"/>, leaving it in <paramref name="state"/>. Use is a prompt, a tool use, or an ask that has the
    /// chat wait for the user; not its start or end (its window opened, reloaded or closed), a turn's end, or the idle
    /// notice a minute after it. Only the chat's own hook events tell it, never which window or tab the user has open, nor
    /// its transcript: that is written without hooks too, when Raven compacts or names the chat headless with hooks off. An
    /// earlier time than the one known changes nothing.
    /// </summary>
    private static DateTimeOffset? UsedAt(SessionSnapshot s, SessionSignal? signal, SessionState state, DateTimeOffset at)
    {
        var use = signal is SessionSignal.PromptSubmit or SessionSignal.ToolUse
            || (signal == SessionSignal.Notification && state == SessionState.Waiting);
        return use && (s.LastUseAt is not { } known || at > known) ? at : s.LastUseAt;
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

    /// <summary>What a lookup of claude <paramref name="Pid"/>'s VS Code window found: its folders, or null for none.</summary>
    private sealed record WindowLookup(int Pid, IReadOnlyList<string>? Folders);

    /// <summary>
    /// Looks up the VS Code window of the chat's claude, once per claude process: two workspaces can share a folder, and
    /// only the window tells which one the chat belongs to. Called outside <see cref="_gate"/>, which it never takes: the
    /// lookup reads the lock folder and the system's tables, and the UI reads snapshots under that lock. Null when there is
    /// nothing to look up (no process, a claimed chat, a process already looked up, a stored PID whose claude is gone) or
    /// the lookup cannot tell yet; that one is tried again with the next event, up to <see cref="MaxWindowTries"/> times,
    /// after which the chat keeps the window it had.
    /// </summary>
    /// <param name="chain">The event's processes, from the hook up: the ones above the claude spare reading every process.</param>
    /// <param name="seenAt">For a PID taken from the chat rather than an event: its last event, to tell a reused PID.</param>
    private WindowLookup? LookUp(string sessionId, int? claudePid, IReadOnlyList<ProcessRef>? chain, DateTimeOffset? seenAt)
    {
        if (_windows is null || claudePid is not { } pid || _claims.ContainsKey(sessionId)
            || (_windowLookups.TryGetValue(sessionId, out var looked) && looked == pid)
            || (seenAt is { } at && !ProcessStillRuns(pid, at)))
        {
            return null;
        }

        if (_windows.TryFoldersOf(pid, Above(chain, pid), out var folders))
        {
            return new WindowLookup(pid, folders);
        }

        var misses = _windowMisses.AddOrUpdate(sessionId, (pid, 1), (_, m) => m.Pid == pid ? (pid, m.Tries + 1) : (pid, 1));
        return misses.Tries >= MaxWindowTries ? new WindowLookup(pid, Folders: null) : null;
    }

    /// <summary>
    /// The window folders the chat has once <paramref name="lookup"/> is taken in: a lookup of its claude records the
    /// process as looked up, and a window it found replaces the one the chat had. The chat keeps what it had without a
    /// lookup, after one of another process (its claude changed meanwhile), once claimed, and when the lookup found no
    /// window (a resume in a terminal, a reused PID).
    /// </summary>
    private IReadOnlyList<string>? WindowOfLocked(SessionSnapshot s, int? claudePid, WindowLookup? lookup)
    {
        if (lookup is null || lookup.Pid != claudePid || _claims.ContainsKey(s.SessionId))
        {
            return s.WindowFolders;
        }

        _windowLookups[s.SessionId] = lookup.Pid;
        return lookup.Folders ?? s.WindowFolders;
    }

    /// <summary>The processes above <paramref name="pid"/> in a hook's chain; null when it is not in the chain or tops it.</summary>
    private static IReadOnlyList<int>? Above(IReadOnlyList<ProcessRef>? chain, int pid)
    {
        if (chain is null)
        {
            return null;
        }

        for (var i = 0; i < chain.Count - 1; i++)
        {
            if (chain[i].Pid == pid)
            {
                return chain.Skip(i + 1).Select(p => p.Pid).ToList();
            }
        }

        return null;
    }

    private static bool SameFolders(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

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
