using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Core.Tests.Sessions;

public class SessionEngineTests
{
    private static readonly Guid AppId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly WorkspaceResolver _resolver = new();
    private readonly List<SessionChanged> _changes = [];
    private readonly SessionEngine _engine;

    public SessionEngineTests()
    {
        _resolver.SetRoots([new WorkspaceRoot(AppId, @"C:\Repo\App")]);
        _engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance);
        _bus.Subscribe<SessionChanged>(_changes.Add);
        _engine.Start();
    }

    private HookEvent Hook(string name, SessionSignal? signal, string session = "s1", string? cwd = @"C:\Repo\App\src",
        string? prompt = null, string? tool = null, IReadOnlyList<ProcessRef>? chain = null) => new()
    {
        SessionId = session,
        EventName = name,
        Signal = signal,
        At = _time.GetUtcNow(),
        Cwd = cwd,
        TranscriptPath = @"C:\Users\me\.claude\projects\c--repo-app\s1.jsonl",
        Prompt = prompt,
        ToolName = tool,
        ParentChain = chain ?? [],
    };

    [Fact]
    public void SessionStart_creates_an_idle_session_mapped_to_its_workspace()
    {
        _bus.Publish(new HookEventReceived(Hook("SessionStart", SessionSignal.SessionStart)));

        var snapshot = _engine.Get("s1").ShouldNotBeNull();
        snapshot.State.ShouldBe(SessionState.Idle);
        snapshot.WorkspaceId.ShouldBe(AppId);
        snapshot.Inferred.ShouldBeFalse();
        snapshot.StartedAt.ShouldBe(_time.GetUtcNow());
        _changes.Count.ShouldBe(1);
        _changes[0].Previous.ShouldBeNull();
        _changes[0].Current.ShouldBe(snapshot);
    }

    [Fact]
    public void Prompt_submit_sets_the_title_once_trimmed_to_60_characters()
    {
        var longPrompt = new string('x', 100);
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit, prompt: longPrompt));
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit, prompt: "second prompt"));

        var snapshot = _engine.Get("s1")!;
        snapshot.State.ShouldBe(SessionState.Working);
        snapshot.Title!.Length.ShouldBe(60);
        snapshot.Title.ShouldEndWith("…");
        snapshot.Title.ShouldStartWith("xxx");
    }

    [Fact]
    public void Tool_use_records_the_tool_name()
    {
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, tool: "Bash"));

        _engine.Get("s1")!.LastToolName.ShouldBe("Bash");
    }

    [Fact]
    public void Unknown_event_is_recorded_but_does_not_change_state()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));
        _time.Advance(TimeSpan.FromMinutes(1));
        _changes.Clear();

        _engine.Apply(Hook("SomeFutureEvent", signal: null));

        var snapshot = _engine.Get("s1")!;
        snapshot.State.ShouldBe(SessionState.Idle);
        snapshot.LastEventAt.ShouldBe(_time.GetUtcNow());
        _changes.Count.ShouldBe(1, "LastEventAt changed, so a snapshot was published");
    }

    [Fact]
    public void Idle_becomes_stale_after_30_minutes_without_events()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));

        _time.Advance(TimeSpan.FromMinutes(29));
        _engine.SweepStale();
        _engine.Get("s1")!.State.ShouldBe(SessionState.Idle);

        _time.Advance(TimeSpan.FromMinutes(1));
        _engine.SweepStale();
        _engine.Get("s1")!.State.ShouldBe(SessionState.Stale);
        _engine.Get("s1")!.StateSince.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public void The_stale_sweep_runs_on_the_timer()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));

        _time.Advance(TimeSpan.FromMinutes(31));

        _engine.Get("s1")!.State.ShouldBe(SessionState.Stale);
    }

    [Fact]
    public void Sessions_seen_before_their_workspace_is_registered_are_re_resolved()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart, cwd: @"D:\Late\Project"));
        _engine.Get("s1")!.WorkspaceId.ShouldBeNull();

        var lateId = Guid.NewGuid();
        _resolver.SetRoots([new WorkspaceRoot(AppId, @"C:\Repo\App"), new WorkspaceRoot(lateId, @"D:\Late\Project")]);
        _bus.Publish(new WorkspaceRootsChanged());

        _engine.Get("s1")!.WorkspaceId.ShouldBe(lateId);
    }

    [Fact]
    public void Transcript_updates_create_inferred_sessions_until_a_hook_event_arrives()
    {
        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s2",
            TranscriptPath = @"C:\t\s2.jsonl",
            ObservedAt = _time.GetUtcNow(),
            Title = "Fix the build",
            Cwd = @"C:\Repo\App",
            Model = "claude-sonnet-5",
            LastActivityAt = _time.GetUtcNow(),
            InferredSignal = SessionSignal.ToolUse,
            LatestContext = new TokenUsage(1000, 50, 200, 3000),
        }));

        var inferred = _engine.Get("s2").ShouldNotBeNull();
        inferred.Inferred.ShouldBeTrue();
        inferred.State.ShouldBe(SessionState.Working);
        inferred.Title.ShouldBe("Fix the build");
        inferred.WorkspaceId.ShouldBe(AppId);
        inferred.Model.ShouldBe("claude-sonnet-5");
        inferred.LatestContext.ContextTokens.ShouldBe(4200);

        _engine.Apply(Hook("Stop", SessionSignal.Stop, session: "s2"));

        var confirmed = _engine.Get("s2")!;
        confirmed.Inferred.ShouldBeFalse();
        confirmed.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void Inferred_signals_are_ignored_once_hooks_have_spoken()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));

        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1",
            TranscriptPath = @"C:\t\s1.jsonl",
            ObservedAt = _time.GetUtcNow(),
            InferredSignal = SessionSignal.Stop,
        });

        _engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_transcript_summary_title_replaces_a_prompt_title_but_not_a_user_rename()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit, prompt: "first prompt"));
        _engine.Apply(new TranscriptUpdate { SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), Title = "Summary title" });
        _engine.Get("s1")!.Title.ShouldBe("Summary title");

        _engine.Rename("s1", "My chat");
        _engine.Apply(new TranscriptUpdate { SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), Title = "Another summary" });

        _engine.Get("s1")!.Title.ShouldBe("My chat");
        _engine.Get("s1")!.TitleLocked.ShouldBeTrue();
    }

    [Fact]
    public void Claude_pid_is_the_first_non_shell_ancestor()
    {
        var chain = new List<ProcessRef>
        {
            new(100, "cmd.exe"), new(200, "claude.exe"), new(300, "Code.exe"), new(400, "explorer.exe"),
        };
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, chain: chain));

        _engine.Get("s1")!.ClaudePid.ShouldBe(200);
    }

    [Fact]
    public void Claude_pid_prefers_node_or_claude_when_present_anywhere_in_the_chain()
    {
        var chain = new List<ProcessRef> { new(100, "bash.exe"), new(150, "sh.exe"), new(200, "node.exe"), new(300, "Code.exe") };
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, chain: chain));

        _engine.Get("s1")!.ClaudePid.ShouldBe(200);
    }

    [Fact]
    public void Process_gone_marks_live_sessions_errored_but_leaves_ended_ones()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));
        _engine.Apply(Hook("SessionEnd", SessionSignal.SessionEnd, session: "s9"));

        _engine.MarkProcessGone("s1");
        _engine.MarkProcessGone("s9");

        _engine.Get("s1")!.State.ShouldBe(SessionState.Errored);
        _engine.Get("s9")!.State.ShouldBe(SessionState.Ended);
    }

    [Fact]
    public void Restore_loads_snapshots_without_publishing()
    {
        var persisted = new SessionSnapshot
        {
            SessionId = "old",
            State = SessionState.Idle,
            StartedAt = _time.GetUtcNow().AddHours(-2),
            LastEventAt = _time.GetUtcNow().AddHours(-1),
            StateSince = _time.GetUtcNow().AddHours(-1),
        };

        _engine.Restore([persisted]);

        _engine.Get("old").ShouldBe(persisted);
        _changes.ShouldBeEmpty();
    }

    [Fact]
    public void Usage_deltas_update_latest_context_and_last_event_time()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));
        var later = _time.GetUtcNow().AddMinutes(2);

        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1",
            TranscriptPath = "p",
            ObservedAt = later,
            LastActivityAt = later,
            Model = "claude-sonnet-5",
            Usage = [new UsageDelta("claude-sonnet-5", later, new TokenUsage(10, 20, 30, 40))],
            LatestContext = new TokenUsage(10, 20, 30, 40),
        });

        var snapshot = _engine.Get("s1")!;
        snapshot.LatestContext.ShouldBe(new TokenUsage(10, 20, 30, 40));
        snapshot.LastEventAt.ShouldBe(later);
        snapshot.Model.ShouldBe("claude-sonnet-5");
    }

    private TranscriptUpdate Update(string session, SessionSignal? inferred, DateTimeOffset? activity = null, bool historical = false) => new()
    {
        SessionId = session,
        TranscriptPath = "p",
        ObservedAt = _time.GetUtcNow(),
        LastActivityAt = activity ?? _time.GetUtcNow(),
        Cwd = @"C:\Repo\App",
        InferredSignal = inferred,
        Historical = historical,
    };

    [Fact]
    public void Quiet_inferred_working_sessions_become_waiting_when_a_tool_call_is_pending_and_idle_otherwise()
    {
        _engine.Apply(Update("w", SessionSignal.ToolUse) with { PendingToolUse = false });
        _engine.Apply(Update("p", SessionSignal.ToolUse) with { PendingToolUse = true });
        _engine.Apply(Update("n", SessionSignal.Notification) with { PendingToolUse = true });
        _engine.Get("w")!.State.ShouldBe(SessionState.Working);
        _engine.Get("p")!.State.ShouldBe(SessionState.Working);
        _engine.Get("n")!.State.ShouldBe(SessionState.Waiting);

        _time.Advance(TimeSpan.FromSeconds(4));
        _engine.SweepStale();
        _engine.Get("w")!.State.ShouldBe(SessionState.Working, "still inside the inferred idle window");

        _time.Advance(TimeSpan.FromSeconds(7));
        _engine.SweepStale();

        _engine.Get("w")!.State.ShouldBe(SessionState.Idle);
        _engine.Get("p")!.State.ShouldBe(SessionState.Waiting, "a pending tool call means Claude is waiting for the user");
        _engine.Get("n")!.State.ShouldBe(SessionState.Waiting, "waiting never decays on the idle timer");
    }

    [Fact]
    public void Inferred_waiting_sessions_without_a_known_process_fall_back_to_idle_after_the_stale_window()
    {
        _engine.Apply(Update("n", SessionSignal.Notification));

        _time.Advance(TimeSpan.FromMinutes(29));
        _engine.SweepStale();
        _engine.Get("n")!.State.ShouldBe(SessionState.Waiting);

        _time.Advance(TimeSpan.FromMinutes(2));
        _engine.SweepStale();
        _engine.Get("n")!.State.ShouldBe(SessionState.Stale, "quiet for the whole stale window: Idle would only last one sweep");
    }

    [Fact]
    public void Restore_keeps_waiting_sessions_waiting_until_their_process_dies_or_a_hook_speaks()
    {
        var old = _time.GetUtcNow().AddMinutes(-3);
        _engine.Restore([new SessionSnapshot { SessionId = "prompt", State = SessionState.Waiting, StartedAt = old, LastEventAt = old, StateSince = old, ClaudePid = 77 }]);

        _engine.Get("prompt")!.State.ShouldBe(SessionState.Waiting, "a permission prompt is quiet but still needs the user");
        _time.Advance(TimeSpan.FromMinutes(45));
        _engine.SweepStale();
        _engine.Get("prompt")!.State.ShouldBe(SessionState.Waiting, "the liveness monitor, not the clock, decides when a hook-backed chat is gone");

        _engine.MarkProcessGone("prompt");
        _engine.Get("prompt")!.State.ShouldBe(SessionState.Errored);
    }

    [Fact]
    public void Concurrent_updates_are_published_in_engine_order_with_increasing_versions()
    {
        var published = new System.Collections.Concurrent.ConcurrentQueue<long>();
        _bus.Subscribe<SessionChanged>(m => published.Enqueue(m.Current.Version));
        var hooks = new Thread(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                _engine.Apply(Hook(i % 2 == 0 ? "Stop" : "PreToolUse", i % 2 == 0 ? SessionSignal.Stop : SessionSignal.ToolUse, tool: "t" + i));
            }
        });
        var transcripts = new Thread(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                _engine.Apply(Update("s1", null) with { LatestContext = new TokenUsage(i, 1, 0, 0) });
            }
        });
        hooks.Start();
        transcripts.Start();
        hooks.Join();
        transcripts.Join();

        var versions = published.ToArray();
        versions.Length.ShouldBeGreaterThan(300);
        for (var i = 1; i < versions.Length; i++)
        {
            versions[i].ShouldBeGreaterThan(versions[i - 1], $"snapshot {i} was published out of order");
        }
    }

    [Fact]
    public void Hook_backed_working_sessions_do_not_decay_on_the_inferred_timer()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));

        _time.Advance(TimeSpan.FromMinutes(5));
        _engine.SweepStale();

        _engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void Restore_downgrades_working_sessions_that_went_quiet_while_the_app_was_down()
    {
        var old = _time.GetUtcNow().AddMinutes(-3);
        _engine.Restore(
        [
            new SessionSnapshot { SessionId = "quiet", State = SessionState.Working, StartedAt = old, LastEventAt = old, StateSince = old },
            new SessionSnapshot { SessionId = "fresh", State = SessionState.Working, StartedAt = old, LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow() },
        ]);

        _engine.Get("quiet")!.State.ShouldBe(SessionState.Idle);
        _engine.Get("fresh")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void Restored_sessions_follow_transcript_inference_until_a_live_hook_event_arrives()
    {
        _engine.Restore([new SessionSnapshot { SessionId = "r", State = SessionState.Idle, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow() }]);

        _engine.Apply(Update("r", SessionSignal.ToolUse));
        _engine.Get("r")!.State.ShouldBe(SessionState.Working);
        _engine.Get("r")!.Inferred.ShouldBeFalse("a restored hook-backed session keeps its marker");

        _engine.Apply(Hook("Stop", SessionSignal.Stop, session: "r"));
        _engine.Apply(Update("r", SessionSignal.ToolUse));

        _engine.Get("r")!.State.ShouldBe(SessionState.Idle, "once a live hook spoke, inference is ignored again");
    }

    [Fact]
    public void Historical_transcript_updates_never_create_sessions_but_still_update_existing_ones()
    {
        _engine.Apply(Update("ancient", SessionSignal.Stop, historical: true));
        _engine.Get("ancient").ShouldBeNull();

        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));
        _engine.Apply(Update("s1", null, historical: true) with { Title = "From history" });

        _engine.Get("s1")!.Title.ShouldBe("From history");
    }

    [Fact]
    public void Hook_backed_working_sessions_go_idle_when_the_transcript_shows_a_user_interrupt()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));
        _engine.Get("s1")!.State.ShouldBe(SessionState.Working);

        _engine.Apply(Update("s1", SessionSignal.Stop) with { Interrupted = true });

        _engine.Get("s1")!.State.ShouldBe(SessionState.Idle, "Esc fires no Stop hook; the transcript's interrupt marker is the only evidence");
        _engine.Get("s1")!.HookSeen.ShouldBeTrue();
    }

    [Fact]
    public void Sessions_left_in_Starting_by_an_unknown_hook_event_decay_to_idle()
    {
        _engine.Apply(Hook("SomethingNew", null));
        _engine.Get("s1")!.State.ShouldBe(SessionState.Starting);

        _time.Advance(TimeSpan.FromSeconds(11));
        _engine.SweepStale();

        _engine.Get("s1")!.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void Subagent_usage_does_not_replace_the_parent_chats_model()
    {
        _engine.Apply(Update("s1", SessionSignal.ToolUse) with { Model = "claude-opus-5" });

        _engine.Apply(Update("s1", null) with
        {
            Usage = [new UsageDelta("claude-haiku-4-5", _time.GetUtcNow(), new TokenUsage(9, 9, 9, 9))],
        });

        _engine.Get("s1")!.Model.ShouldBe("claude-opus-5", "a sub-agent's usage names the sub-agent's model; the context bar is measured against the parent's");
    }

    private sealed class DeadPidProbe(params int[] dead) : IProcessProbe
    {
        public bool IsAlive(int pid) => !dead.Contains(pid);
    }

    [Fact]
    public void Restore_checks_saved_claude_pids_and_drops_sessions_whose_process_is_gone_to_idle()
    {
        using var engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance, probe: new DeadPidProbe(77));
        var old = _time.GetUtcNow().AddMinutes(-3);
        engine.Restore(
        [
            new SessionSnapshot { SessionId = "gone", State = SessionState.Waiting, StartedAt = old, LastEventAt = old, StateSince = old, ClaudePid = 77 },
            new SessionSnapshot { SessionId = "alive", State = SessionState.Waiting, StartedAt = old, LastEventAt = old, StateSince = old, ClaudePid = 88 },
        ]);

        engine.Get("gone")!.State.ShouldBe(SessionState.Idle, "the turn ended while the app was down; a red Errored row would blame the wrong thing");
        engine.Get("gone")!.ClaudePid.ShouldBeNull("a dead PID must not be matched against whatever process reuses it");
        engine.Get("alive")!.State.ShouldBe(SessionState.Waiting);
        engine.Get("alive")!.ClaudePid.ShouldBe(88);
    }

    [Fact]
    public void Repeated_transcript_writes_do_not_restart_the_working_timer_without_hooks()
    {
        var started = _time.GetUtcNow();
        _engine.Apply(Update("s1", SessionSignal.ToolUse));
        _engine.Get("s1")!.StateSince.ShouldBe(started);

        _time.Advance(TimeSpan.FromSeconds(3));
        _engine.Apply(Update("s1", SessionSignal.ToolUse));

        _engine.Get("s1")!.State.ShouldBe(SessionState.Working);
        _engine.Get("s1")!.StateSince.ShouldBe(started, "the elapsed timer measures the turn, not the last transcript write");
    }

    [Fact]
    public void An_interrupt_marker_older_than_the_current_hook_state_is_ignored()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));
        var promptAt = _time.GetUtcNow();

        _engine.Apply(Update("s1", SessionSignal.Stop, activity: promptAt.AddSeconds(-5)) with { Interrupted = true });
        _engine.Get("s1")!.State.ShouldBe(SessionState.Working, "that Esc belonged to the previous turn");

        _engine.Apply(Update("s1", SessionSignal.Stop, activity: promptAt.AddSeconds(1)) with { Interrupted = true });
        _engine.Get("s1")!.State.ShouldBe(SessionState.Idle);
    }
}
