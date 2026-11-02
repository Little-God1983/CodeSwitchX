using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Core.Tests.Sessions;

/// <summary>#332: a chat that ended its turn with a background job open works on until the job ends.</summary>
public sealed class SessionEngineJobsTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly SessionEngine _engine;

    public SessionEngineJobsTests()
    {
        _engine = new SessionEngine(_bus, new WorkspaceResolver(), _time, NullLogger<SessionEngine>.Instance);
        _engine.Start();
    }

    public void Dispose() => _engine.Dispose();

    private HookEvent Hook(string name, SessionSignal? signal, string? job = null, string? agent = null, string? source = null) => new()
    {
        SessionId = "s1", EventName = name, Signal = signal, At = _time.GetUtcNow(), BackgroundJob = job, AgentId = agent, Source = source,
    };

    private void Prompt() => _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));

    private void JobStarts(string job, string? agent = null) => _engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, job, agent));

    private void Stop() => _engine.Apply(Hook("Stop", SessionSignal.Stop));

    private void TranscriptTells(BackgroundJobEvent[]? started = null, BackgroundJobEvent[]? ended = null, bool historical = false) => _engine.Apply(new TranscriptUpdate
    {
        SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(),
        JobsStarted = started ?? [], JobsEnded = ended ?? [], Historical = historical,
    });

    private void JobEnds(string job) => TranscriptTells(ended: [new BackgroundJobEvent(job, _time.GetUtcNow())]);

    private SessionSnapshot Chat => _engine.Get("s1").ShouldNotBeNull();

    private void ATurnEndsWithAJobOpen(string job = "job1")
    {
        Prompt();
        JobStarts(job);
        Stop();
    }

    [Fact]
    public void A_chat_whose_turn_ends_with_a_job_open_works_on()
    {
        ATurnEndsWithAJobOpen();

        Chat.State.ShouldBe(SessionState.Working);
        Chat.OpenJobs.ShouldBe(["job1"]);
        Chat.TurnOverAt.ShouldBe(_time.GetUtcNow(), "it sits at its input prompt");
    }

    [Fact]
    public void A_chat_with_no_job_open_goes_idle_as_ever()
    {
        Prompt();
        Stop();

        Chat.State.ShouldBe(SessionState.Idle);
        Chat.TurnOverAt.ShouldBeNull();
    }

    [Fact]
    public void A_job_that_ended_within_the_turn_holds_nothing()
    {
        Prompt();
        JobStarts("job1");
        JobEnds("job1");
        Stop();

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void After_its_last_job_the_chat_works_on_until_the_turn_that_reacts_to_it_ends()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(10));

        JobEnds("job1");
        Chat.State.ShouldBe(SessionState.Working, "Claude Code starts a turn on the job's end");
        Chat.OpenJobs.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(9));
        Stop();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_stop_that_came_after_the_job_ended_but_before_its_end_was_read_ends_it_at_once()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(10));
        var ended = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(1));
        Stop();
        Chat.State.ShouldBe(SessionState.Working, "the job's end is not read yet");

        _time.Advance(TimeSpan.FromSeconds(1));
        TranscriptTells(ended: [new BackgroundJobEvent("job1", ended)]);

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_job_end_no_turn_reacts_to_ends_the_chat_after_the_settling_time()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(10));
        JobEnds("job1");

        _time.Advance(SessionEngine.JobEndSettle - TimeSpan.FromSeconds(1));
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Working);

        _time.Advance(TimeSpan.FromSeconds(2));
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_reaction_that_uses_tools_is_no_longer_settling()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(10));
        JobEnds("job1");
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse));

        _time.Advance(SessionEngine.JobEndSettle + TimeSpan.FromMinutes(1));
        _engine.SweepStale();

        Chat.State.ShouldBe(SessionState.Working, "a long foreground command of that turn stays working");
    }

    [Fact]
    public void Only_the_last_of_several_jobs_lets_the_chat_go()
    {
        Prompt();
        JobStarts("job1");
        JobStarts("job2");
        Stop();

        JobEnds("job1");
        Chat.OpenJobs.ShouldBe(["job2"]);
        Chat.TurnOverAt.ShouldNotBeNull();
        Stop();
        Chat.State.ShouldBe(SessionState.Working);

        _time.Advance(TimeSpan.FromSeconds(1));
        JobEnds("job2");
        _time.Advance(TimeSpan.FromSeconds(1));
        Stop();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void There_is_no_limit_on_how_long_a_job_holds_a_chat()
    {
        ATurnEndsWithAJobOpen();

        _time.Advance(TimeSpan.FromDays(2));
        _engine.SweepStale();

        Chat.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_new_prompt_means_the_turn_is_not_over_any_more()
    {
        ATurnEndsWithAJobOpen();

        Prompt();

        Chat.State.ShouldBe(SessionState.Working);
        Chat.TurnOverAt.ShouldBeNull();
        Chat.OpenJobs.ShouldBe(["job1"]);
    }

    [Fact]
    public void A_background_agent_s_tool_use_leaves_the_turn_over()
    {
        ATurnEndsWithAJobOpen();

        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, agent: "a1"));

        Chat.TurnOverAt.ShouldNotBeNull("the chat is still at its input prompt");
    }

    [Fact]
    public void A_job_a_sub_agent_starts_is_not_the_chat_s()
    {
        Prompt();
        JobStarts("job1", agent: "a1");
        Stop();

        Chat.State.ShouldBe(SessionState.Idle, "its end may never reach the chat's own transcript");
    }

    [Fact]
    public void The_idle_prompt_notice_does_not_end_a_chat_with_a_job_open()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));

        _engine.Apply(Hook("Notification", SessionSignal.IdlePrompt));

        Chat.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void Esc_ends_the_turn_but_not_the_job()
    {
        Prompt();
        JobStarts("job1");
        _time.Advance(TimeSpan.FromSeconds(1));

        _engine.Apply(new TranscriptUpdate { SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(), Interrupted = true });

        Chat.State.ShouldBe(SessionState.Working);
        Chat.TurnOverAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("clear")]
    public void A_new_process_clears_the_jobs(string source)
    {
        ATurnEndsWithAJobOpen();

        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart, source: source));

        Chat.State.ShouldBe(SessionState.Idle);
        Chat.OpenJobs.ShouldBeEmpty();
        Chat.TurnOverAt.ShouldBeNull();
    }

    [Fact]
    public void A_compact_keeps_the_jobs_since_the_process_and_its_jobs_live_on()
    {
        ATurnEndsWithAJobOpen();

        _engine.Apply(Hook("SessionStart", null, source: "compact") with { Informational = true });

        Chat.State.ShouldBe(SessionState.Working);
        Chat.OpenJobs.ShouldBe(["job1"]);
    }

    [Fact]
    public void The_chat_s_end_clears_the_jobs()
    {
        ATurnEndsWithAJobOpen();

        _engine.Apply(Hook("SessionEnd", SessionSignal.SessionEnd));

        Chat.State.ShouldBe(SessionState.Ended);
        Chat.OpenJobs.ShouldBeEmpty();
    }

    [Fact]
    public void Its_process_going_clears_the_jobs()
    {
        _engine.Restore([new SessionSnapshot
        {
            SessionId = "s1", State = SessionState.Working, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(),
            ClaudePid = 77, OpenJobs = ["job1"], TurnOverAt = _time.GetUtcNow(),
        }]);

        _engine.MarkProcessGone("s1", 77, Chat.LastEventAt).ShouldBeTrue();

        Chat.State.ShouldBe(SessionState.Errored);
        Chat.OpenJobs.ShouldBeEmpty();
        Chat.TurnOverAt.ShouldBeNull();
    }

    [Fact]
    public void A_hook_that_arrives_after_the_job_s_end_was_read_opens_nothing()
    {
        Prompt();
        JobEnds("job1");

        JobStarts("job1");
        Stop();

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void The_transcript_tells_a_job_the_hook_did_not_and_the_same_job_counts_once()
    {
        Prompt();
        TranscriptTells(started: [new BackgroundJobEvent("job1", _time.GetUtcNow())]);
        JobStarts("job1");
        TranscriptTells(started: [new BackgroundJobEvent("job2", _time.GetUtcNow())]);

        Chat.OpenJobs.ShouldBe(["job1", "job2"]);
    }

    [Fact]
    public void A_copy_of_an_old_job_start_by_claude_resume_opens_nothing()
    {
        Prompt();
        TranscriptTells(started: [new BackgroundJobEvent("old", Chat.StartedAt.AddHours(-3))]);

        Chat.OpenJobs.ShouldBeEmpty();
    }

    [Fact]
    public void Old_transcript_lines_open_no_job()
    {
        Prompt();
        TranscriptTells(started: [new BackgroundJobEvent("old", _time.GetUtcNow())], historical: true);

        Chat.OpenJobs.ShouldBeEmpty();
    }

    [Fact]
    public void A_chat_nothing_can_tell_the_life_of_opens_no_job_from_its_transcript()
    {
        TranscriptTells(started: [new BackgroundJobEvent("job1", _time.GetUtcNow())]);

        Chat.OpenJobs.ShouldBeEmpty("no hook and no process: nothing would ever clear a job with no end marker");
    }

    private SessionEngine Restored(SessionSnapshot chat, FakeProcessProbe probe)
    {
        var engine = new SessionEngine(_bus, new WorkspaceResolver(), _time, NullLogger<SessionEngine>.Instance, probe: probe);
        engine.Restore([chat]);
        engine.Start();
        return engine;
    }

    private SessionSnapshot Stored(int? pid = 77) => new()
    {
        SessionId = "s1", State = SessionState.Working, StartedAt = _time.GetUtcNow().AddHours(-1), LastEventAt = _time.GetUtcNow().AddMinutes(-5),
        StateSince = _time.GetUtcNow().AddMinutes(-5), ClaudePid = pid, Title = "t",
    };

    [Fact]
    public void An_open_job_survives_a_restart_while_its_process_runs()
    {
        using var engine = Restored(Stored() with { OpenJobs = ["job1"], TurnOverAt = _time.GetUtcNow().AddMinutes(-5) }, new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

        _time.Advance(TimeSpan.FromMinutes(5));
        engine.SweepStale();

        engine.Get("s1")!.State.ShouldBe(SessionState.Working);
        engine.Get("s1")!.OpenJobs.ShouldBe(["job1"]);
    }

    [Fact]
    public void A_restart_clears_the_jobs_of_a_process_that_is_gone()
    {
        using var engine = Restored(Stored() with { OpenJobs = ["job1"], TurnOverAt = _time.GetUtcNow().AddMinutes(-5) }, new FakeProcessProbe());

        engine.Get("s1")!.State.ShouldBe(SessionState.Idle);
        engine.Get("s1")!.OpenJobs.ShouldBeEmpty();
        engine.Get("s1")!.TurnOverAt.ShouldBeNull();
    }

    [Fact]
    public void A_running_foreground_command_stays_working_through_a_restart()
    {
        using var engine = Restored(Stored() with { AwaitingToolResult = true }, new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

        _time.Advance(TimeSpan.FromMinutes(5));
        engine.SweepStale();

        engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_restored_foreground_command_whose_transcript_says_it_is_over_decays_as_ever()
    {
        using var engine = Restored(Stored() with { AwaitingToolResult = true }, new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

        engine.Apply(new TranscriptUpdate { SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(), PendingToolUse = false });
        _time.Advance(TimeSpan.FromMinutes(1));
        engine.SweepStale();

        engine.Get("s1")!.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_restored_working_chat_with_no_tool_open_goes_idle_as_before()
    {
        using var engine = Restored(Stored(), new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

        engine.Get("s1")!.State.ShouldBe(SessionState.Idle, "its Stop may have been lost while the app was closed, and no later line would say so");
    }
}
