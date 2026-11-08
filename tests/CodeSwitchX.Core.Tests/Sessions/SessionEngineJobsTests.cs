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

    private void TaskStop(string job, string? agent = null) => _engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: agent) with { EndedJob = job });

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
    public void A_job_the_chat_stops_ends_at_once_so_the_turn_s_stop_leaves_it_idle()
    {
        // #381: the Stop follows the TaskStop sooner than the transcript is read.
        ATurnEndsWithAJobOpen();
        Prompt();
        TaskStop("job1");
        Chat.OpenJobs.ShouldBeEmpty();

        Stop();

        Chat.State.ShouldBe(SessionState.Idle);
        Chat.TurnOverAt.ShouldBeNull();
        Chat.JobsEndedAt.ShouldBeNull("no turn reacts to a stop the chat made itself");
    }

    [Fact]
    public void The_transcript_s_later_word_on_a_stop_the_hook_told_changes_nothing()
    {
        Prompt();
        JobStarts("job1");
        TaskStop("job1");
        Stop();

        _time.Advance(TimeSpan.FromSeconds(2));
        TranscriptTells(started: [new BackgroundJobEvent("job1", _time.GetUtcNow())], ended: [new BackgroundJobEvent("job1", _time.GetUtcNow())]);

        Chat.State.ShouldBe(SessionState.Idle);
        Chat.OpenJobs.ShouldBeEmpty();
    }

    [Fact]
    public void A_start_told_after_the_hook_s_stop_opens_nothing()
    {
        Prompt();
        TaskStop("job1");

        TranscriptTells(started: [new BackgroundJobEvent("job1", _time.GetUtcNow())]);
        JobStarts("job1");
        Stop();

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void Stopping_one_of_two_jobs_leaves_the_other_holding_the_chat()
    {
        Prompt();
        JobStarts("job1");
        JobStarts("job2");
        TaskStop("job1");
        Stop();

        Chat.State.ShouldBe(SessionState.Working);
        Chat.OpenJobs.ShouldBe(["job2"]);
    }

    [Fact]
    public void A_sub_agent_s_stop_ends_the_chat_s_job_too()
    {
        Prompt();
        JobStarts("job1");
        TaskStop("job1", agent: "a1");
        Chat.OpenJobs.ShouldBeEmpty("the transcript counts a sub-agent's stop as well");

        Stop();

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_sub_agent_that_stops_the_last_job_after_the_turn_leaves_the_chat_idle()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));

        TaskStop("job1", agent: "a1");

        Chat.State.ShouldBe(SessionState.Idle, "no turn reacts to a stop: it tells no task notification");
        Chat.OpenJobs.ShouldBeEmpty();
        Chat.TurnOverAt.ShouldBeNull();
        Chat.JobsEndedAt.ShouldBeNull();
    }

    [Fact]
    public void A_stop_whose_hook_lands_after_the_turn_s_stop_leaves_the_chat_idle()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromSeconds(1));

        TaskStop("job1");

        Chat.State.ShouldBe(SessionState.Idle);
        Chat.TurnOverAt.ShouldBeNull();
        Chat.JobsEndedAt.ShouldBeNull();
    }

    [Fact]
    public void A_late_stop_of_one_of_two_jobs_leaves_the_turn_over()
    {
        Prompt();
        JobStarts("job1");
        JobStarts("job2");
        Stop();
        var over = Chat.TurnOverAt.ShouldNotBeNull();
        _time.Advance(TimeSpan.FromSeconds(1));

        TaskStop("job1");

        Chat.State.ShouldBe(SessionState.Working);
        Chat.OpenJobs.ShouldBe(["job2"]);
        Chat.TurnOverAt.ShouldBe(over, "the stop ends a step of the turn that is over, it opens no new one");

        _time.Advance(TimeSpan.FromSeconds(1));
        JobEnds("job2");
        Chat.JobsEndedAt.ShouldNotBeNull("the last job's end is settled as ever");
    }

    [Fact]
    public void A_background_agent_s_next_step_leaves_a_settling_end_as_it_is()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");

        _time.Advance(TimeSpan.FromSeconds(5));
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, agent: "a1"));
        Chat.JobsEndedAt.ShouldNotBeNull("its steps say nothing about the chat's own turn");

        _time.Advance(SessionEngine.JobEndSettle + TimeSpan.FromSeconds(1));
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_stop_of_a_job_the_chat_never_told_changes_nothing_but_is_remembered()
    {
        ATurnEndsWithAJobOpen();

        TaskStop("other");
        Chat.OpenJobs.ShouldBe(["job1"]);

        JobStarts("other");
        Chat.OpenJobs.ShouldBe(["job1"]);
    }

    private void MainPre(string step) => _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse) with { ToolUseId = step });

    private void MainPost(string step, string? job = null, string? ended = null) =>
        _engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, job) with { ToolUseId = step, EndedJob = ended });

    [Fact]
    public void A_job_start_told_after_the_turn_s_stop_keeps_the_turn_over()
    {
        // #397: the last step starts the job, and its PostToolUse lands after the Stop. It counted as the turn going on and left
        // no turn-over mark, so once the job ended with no reacting turn the chat stayed Working for good.
        Prompt();
        MainPre("t1");
        Stop();
        Chat.State.ShouldBe(SessionState.Idle);

        MainPost("t1", job: "job1");
        Chat.State.ShouldBe(SessionState.Working, "a job is open");
        Chat.TurnOverAt.ShouldNotBeNull("the turn was over before the job's start was told");

        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");
        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_step_result_told_after_the_turn_s_stop_leaves_the_turn_over()
    {
        // #397: with a job open, the last step's late PostToolUse erased the turn-over mark the Stop had set.
        Prompt();
        MainPre("t0");
        MainPost("t0", job: "job1");
        MainPre("t1");
        Stop();
        var over = Chat.TurnOverAt.ShouldNotBeNull();

        MainPost("t1");

        Chat.State.ShouldBe(SessionState.Working);
        Chat.TurnOverAt.ShouldBe(over);
    }

    [Fact]
    public void A_stop_s_result_told_after_the_transcript_ended_the_job_leaves_the_chat_idle()
    {
        // #396: the transcript told the TaskStop's result between the turn's Stop and the stop's own late PostToolUse, which
        // then made the idle chat Working with nothing left to end that.
        Prompt();
        MainPre("t0");
        MainPost("t0", job: "job1");
        MainPre("t1");
        var stopped = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(1));
        Stop();
        TranscriptTells(ended: [new BackgroundJobEvent("job1", stopped)]);
        Chat.State.ShouldBe(SessionState.Idle);

        MainPost("t1", ended: "job1");

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_stop_s_result_told_after_the_turn_s_stop_still_ends_the_job()
    {
        // #396/#397: a late result is no progress, but the end of a job it tells still counts.
        Prompt();
        MainPre("t0");
        MainPost("t0", job: "job1");
        MainPre("t1");
        Stop();

        MainPost("t1", ended: "job1");

        Chat.State.ShouldBe(SessionState.Idle);
        Chat.OpenJobs.ShouldBeEmpty();
    }

    [Fact]
    public void A_step_result_with_no_job_told_after_the_turn_s_stop_leaves_the_chat_idle()
    {
        Prompt();
        MainPre("t1");
        Stop();

        MainPost("t1");

        Chat.State.ShouldBe(SessionState.Idle, "the late result is no progress");
    }

    [Fact]
    public void A_step_result_told_after_a_failed_turn_leaves_the_chat_idle()
    {
        Prompt();
        MainPre("t1");
        _engine.Apply(Hook("StopFailure", SessionSignal.Stop));

        MainPost("t1");

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_job_start_told_late_marks_the_turn_over_at_the_stop()
    {
        // Review round 1 of #396/#397: the mark was the late result's own time, so a job that ended between the Stop and that
        // result counted as told to the turn already, and the chat went Idle before the turn that reacts to the end.
        Prompt();
        MainPre("t1");
        var stoppedAt = _time.GetUtcNow();
        Stop();
        _time.Advance(TimeSpan.FromMilliseconds(900));

        MainPost("t1", job: "job1");
        Chat.TurnOverAt.ShouldBe(stoppedAt);

        TranscriptTells(ended: [new BackgroundJobEvent("job1", stoppedAt.AddMilliseconds(500))]);
        Chat.State.ShouldBe(SessionState.Working, "the end came after the turn: a turn that reacts to it may come");
        Chat.JobsEndedAt.ShouldNotBeNull();
    }

    [Fact]
    public void A_job_start_told_late_while_a_background_agent_s_prompt_waits_gets_the_wait()
    {
        // Review round 1 of #396/#397: a late job start on a chat held Waiting set no turn-over mark, so its end began no wait,
        // and answering the prompt made the chat Idle at once, before the turn that reacts to the end.
        Prompt();
        AgentAsks("a1");
        MainPre("t1");
        Stop();
        MainPost("t1", job: "job1");
        Chat.TurnOverAt.ShouldNotBeNull();

        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");
        AgentSteps("a1");
        Chat.State.ShouldBe(SessionState.Working, "the turn that reacts to the end may still come");

        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_step_start_told_after_the_turn_s_stop_leaves_the_turn_over()
    {
        // Review round 2 of #396/#397: a quick step's PreToolUse lands after its own result and the Stop. It counted as the
        // main agent stepping again: it cleared the turn-over mark and the stop, so the chat stayed Working with the job open.
        Prompt();
        MainPre("t0");
        MainPost("t0", job: "job1");
        MainPost("t1");
        Stop();
        var over = Chat.TurnOverAt.ShouldNotBeNull();

        MainPre("t1");

        Chat.State.ShouldBe(SessionState.Working);
        Chat.TurnOverAt.ShouldBe(over);
    }

    [Fact]
    public void A_step_start_told_after_the_turn_s_stop_leaves_an_idle_chat_idle()
    {
        Prompt();
        MainPost("t1");
        Stop();

        MainPre("t1");

        Chat.State.ShouldBe(SessionState.Idle);
        Chat.AwaitingToolResult.ShouldBeFalse("the step's result came already");
    }

    [Fact]
    public void A_compaction_keeps_what_tells_a_late_step()
    {
        // Review round 3 of #396/#397: a SessionStart after compaction, which keeps the chat's state, forgot the steps whose
        // result came, so the late start of one after the Stop counted as the turn going on.
        Prompt();
        MainPost("t1");
        _engine.Apply(Hook("SessionStart", null, source: "compact") with { Informational = true });
        Stop();

        MainPre("t1");

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_step_result_told_before_its_start_ends_no_other_step()
    {
        // Review round 3 of #396/#397: a result whose step was not open yet ended the main agent's last open step instead, a
        // step that a nameless permission prompt then could not be put down to: it went to a background agent.
        Prompt();
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, agent: "a1") with { ToolUseId = "s1" });
        MainPre("tA");
        MainPost("tB");
        MainPre("tB");

        _engine.Apply(Hook("Notification", SessionSignal.Notification) with { NotificationType = "permission_prompt" });
        Chat.State.ShouldBe(SessionState.Waiting);
        AgentSteps("a1");

        Chat.State.ShouldBe(SessionState.Waiting, "the prompt is the main agent's, for step tA, which is still open");
    }

    [Fact]
    public void A_prompt_after_the_stop_makes_every_result_count_again()
    {
        Prompt();
        MainPre("t1");
        Stop();

        Prompt();
        MainPost("t2");
        MainPre("t2");

        Chat.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_step_start_told_after_its_own_result_works_on_and_the_stop_ends_the_turn()
    {
        // #396/#397: the reverse race, a PreToolUse that lands after its own PostToolUse.
        Prompt();
        MainPost("t1");
        MainPre("t1");
        Chat.State.ShouldBe(SessionState.Working);

        Stop();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void The_main_agent_s_step_after_its_stop_is_a_turn_again()
    {
        // #396/#397: a turn that reacts to a job's end starts with no prompt; its steps are progress, not late results.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));

        MainPre("t2");
        MainPost("t2");

        Chat.State.ShouldBe(SessionState.Working);
        Chat.TurnOverAt.ShouldBeNull("the turn is on again");
    }

    private void AgentAsks(string agent) => _engine.Apply(Hook("PermissionRequest", SessionSignal.Notification, agent: agent));

    private void AgentSteps(string agent) => _engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: agent));

    [Fact]
    public void A_background_agent_s_prompt_leaves_the_turn_over()
    {
        ATurnEndsWithAJobOpen();
        var over = Chat.TurnOverAt;

        AgentAsks("a1");

        Chat.State.ShouldBe(SessionState.Waiting);
        Chat.TurnOverAt.ShouldBe(over, "the main agent's turn is over: it is a background agent that asks");
    }

    [Fact]
    public void A_job_that_ended_while_a_background_agent_s_prompt_waited_settles_once_the_prompt_is_answered()
    {
        // #390: before, the prompt cleared the turn-over mark, and the chat stayed Working with no job open for good.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        AgentAsks("a1");
        var ended = _time.GetUtcNow();
        JobEnds("job1");

        Chat.State.ShouldBe(SessionState.Waiting);
        Chat.TurnOverAt.ShouldBeNull("no job is open: no turn-over mark is left behind, not even through a restart");
        Chat.JobsEndedAt.ShouldBe(ended, "the wait counts from the end itself");

        _time.Advance(TimeSpan.FromSeconds(10));
        AgentSteps("a1");
        Chat.State.ShouldBe(SessionState.Working, "a turn that reacts to the end may still come");

        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_background_agent_s_prompt_during_the_wait_keeps_it()
    {
        // Review round 1 of #390: a second prompt cleared the wait, and the chat stayed Working once it was answered.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");
        var ended = Chat.JobsEndedAt;

        _time.Advance(TimeSpan.FromSeconds(10));
        AgentAsks("a1");
        Chat.JobsEndedAt.ShouldBe(ended);
        AgentSteps("a1");
        Chat.JobsEndedAt.ShouldBe(_time.GetUtcNow(), "a turn that reacts may have waited behind the prompt: its wait begins at the answer");

        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_reacting_turn_that_began_while_a_prompt_waited_is_not_cut_off()
    {
        // Review round 1 of #390: the chat's own request after the end, made while the background agent's prompt waited.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        AgentAsks("a1");
        JobEnds("job1");
        _time.Advance(TimeSpan.FromSeconds(5));
        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(), LastRequestAt = _time.GetUtcNow(),
        });

        _time.Advance(TimeSpan.FromSeconds(10));
        AgentSteps("a1");
        _time.Advance(SessionEngine.JobEndSettle + TimeSpan.FromMinutes(1));
        _engine.SweepStale();

        Chat.State.ShouldBe(SessionState.Working, "the reacting turn ends with its own stop");
    }

    [Fact]
    public void A_stop_while_a_background_agent_s_prompt_waits_settles_once_it_is_answered()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        AgentAsks("a2");

        TaskStop("job1", agent: "a1");
        Chat.State.ShouldBe(SessionState.Waiting);
        Chat.TurnOverAt.ShouldBeNull();
        Chat.JobsEndedAt.ShouldNotBeNull();

        AgentSteps("a2");
        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void An_idle_prompt_told_stale_by_a_background_agent_s_step_keeps_the_wait()
    {
        // Review round 2 of #390: the stale notice counted as the main agent's turn, cleared the wait and left the state.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromSeconds(30));
        JobEnds("job1");
        var ended = Chat.JobsEndedAt;

        _time.Advance(TimeSpan.FromSeconds(25));
        AgentSteps("a1");
        _time.Advance(TimeSpan.FromSeconds(5));
        _engine.Apply(Hook("Notification", SessionSignal.IdlePrompt));

        Chat.State.ShouldBe(SessionState.Working);
        Chat.JobsEndedAt.ShouldBe(ended, "a stale notice tells nothing new");
        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void The_main_agent_s_step_while_a_background_agent_s_prompt_waits_is_the_reacting_turn()
    {
        // Review round 2 of #390: the step's signal is no progress for the Waiting, but it is the main agent's turn all the
        // same; read from the hooks alone, the wait ran out once the prompt was answered and cut that turn off.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        AgentAsks("a1");
        JobEnds("job1");
        _time.Advance(TimeSpan.FromSeconds(5));

        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse));
        Chat.State.ShouldBe(SessionState.Waiting);
        Chat.JobsEndedAt.ShouldBeNull("the turn that reacts has begun");

        _time.Advance(TimeSpan.FromMinutes(3));
        AgentSteps("a1");
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Working, "the reacting turn ends with its own stop");
    }

    [Fact]
    public void A_job_the_main_agent_starts_while_a_background_agent_s_prompt_waits_keeps_the_chat_working()
    {
        // Review round 2 of #390: the old end's wait ran out with the new job open, and the chat went Idle.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        AgentAsks("a1");
        JobEnds("job1");
        _time.Advance(TimeSpan.FromSeconds(5));

        JobStarts("job2");
        _time.Advance(TimeSpan.FromMinutes(3));
        AgentSteps("a1");
        _engine.SweepStale();

        Chat.State.ShouldBe(SessionState.Working);
        Chat.OpenJobs.ShouldBe(["job2"]);
    }

    [Fact]
    public void A_job_open_again_is_not_cut_off_by_the_wait_of_the_one_before()
    {
        // Review round 2 of #390: the transcript tells the new job's start before any hook of its turn.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");
        _time.Advance(TimeSpan.FromSeconds(5));

        TranscriptTells(started: [new BackgroundJobEvent("job2", _time.GetUtcNow())]);
        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();

        Chat.State.ShouldBe(SessionState.Working);
        Chat.OpenJobs.ShouldBe(["job2"]);

        // Review round 3: the old end's wait outlived the new job, and cut the chat off the moment that job ended.
        _time.Advance(TimeSpan.FromMinutes(10));
        JobEnds("job2");
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Working, "the turn that started job2 has not stopped yet");
    }

    [Fact]
    public void A_prompt_answered_late_gives_the_reacting_turn_its_wait()
    {
        // Review round 3 of #390: the wait counted from the end alone, so a prompt answered after two minutes left the turn
        // that reacts (held back behind the prompt) no time at all.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        AgentAsks("a1");
        JobEnds("job1");

        _time.Advance(TimeSpan.FromMinutes(5));
        AgentSteps("a1");
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Working);

        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void The_main_agent_s_step_after_its_stop_while_a_prompt_waits_is_its_turn_again()
    {
        // Review round 3 of #390: the step left the turn marked ended, so answering the prompt stopped the chat mid-turn.
        Prompt();
        JobStarts("job1");
        AgentAsks("a1");
        Stop();
        JobEnds("job1");

        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse));
        AgentSteps("a1");

        Chat.State.ShouldBe(SessionState.Working, "the main agent's turn goes on");
    }

    [Fact]
    public void The_main_agent_s_step_result_told_after_its_stop_while_a_prompt_waits_leaves_the_turn_over()
    {
        // Review round 4 of #390: a late PostToolUse ends a step its PreToolUse began before the Stop; it undid the turn's end,
        // and once the prompt was answered the chat worked on with no Stop to come.
        Prompt();
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse));
        AgentAsks("a1");
        Stop();
        _engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse));

        AgentSteps("a1");

        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_job_that_ends_after_a_stop_told_while_a_prompt_waits_gets_its_wait()
    {
        // Review round 4 of #390: that stop recorded no turn-over, so the end started no wait, and answering the prompt made
        // the chat Idle at once, before the turn that reacts.
        Prompt();
        JobStarts("job1");
        AgentAsks("a1");
        Stop();
        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");
        _time.Advance(TimeSpan.FromMinutes(1));

        AgentSteps("a1");
        Chat.State.ShouldBe(SessionState.Working, "the turn that reacts to the end may still come");

        _time.Advance(SessionEngine.JobEndSettle);
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void The_main_agent_s_own_prompt_is_a_turn_again()
    {
        Prompt();
        JobStarts("job1");
        _engine.Apply(Hook("PermissionRequest", SessionSignal.Notification));

        Chat.State.ShouldBe(SessionState.Waiting);
        Chat.TurnOverAt.ShouldBeNull();
    }

    [Fact]
    public void A_busy_background_agent_does_not_put_off_the_end_of_the_wait()
    {
        // #390: the wait counted from the chat's last event, which each of the agent's steps moved on.
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");

        // A step every 30 s, the last one a second before the two minutes are up.
        foreach (var gap in new[] { 30, 30, 30, 29 })
        {
            _time.Advance(TimeSpan.FromSeconds(gap));
            AgentSteps("a1");
            Chat.State.ShouldBe(SessionState.Working);
        }

        _time.Advance(TimeSpan.FromSeconds(1));
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Idle, "two minutes after the end, though the agent stepped a moment ago");
    }

    [Fact]
    public void A_request_of_the_chat_s_own_after_the_end_is_the_reacting_turn_and_ends_the_wait()
    {
        ATurnEndsWithAJobOpen();
        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");
        _time.Advance(TimeSpan.FromSeconds(5));

        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(), LastRequestAt = _time.GetUtcNow(),
        });

        Chat.JobsEndedAt.ShouldBeNull("that turn ends with its own stop");
        _time.Advance(SessionEngine.JobEndSettle + TimeSpan.FromMinutes(1));
        _engine.SweepStale();
        Chat.State.ShouldBe(SessionState.Working, "a long reply that uses no tool is still the chat at work");

        Stop();
        Chat.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_request_from_before_the_end_does_not_end_the_wait()
    {
        ATurnEndsWithAJobOpen();
        var before = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromMinutes(1));
        JobEnds("job1");

        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(), LastRequestAt = before,
        });

        Chat.JobsEndedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Only_the_newest_ends_are_remembered()
    {
        // #390: a start told late comes seconds after its end, so the newest ends are enough; the oldest are let go.
        Prompt();
        for (var i = 0; i <= SessionEngine.RecentIds.JobsKept; i++)
        {
            TaskStop($"j{i}");
        }

        JobStarts($"j{SessionEngine.RecentIds.JobsKept}");
        Chat.OpenJobs.ShouldBeEmpty("the newest end is remembered");

        JobStarts("j0");
        Chat.OpenJobs.ShouldBe(["j0"], "the oldest was let go");
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
    public void The_wait_for_a_reacting_turn_survives_a_restart_while_a_background_agent_s_prompt_waits()
    {
        // #395: the wait was not stored, so the chat came back Waiting with neither mark, and answering the prompt left it
        // Working with nothing to end that.
        var ended = _time.GetUtcNow().AddMinutes(-5);
        using var engine = Restored(Stored() with { State = SessionState.Waiting, JobsEndedAt = ended }, new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));
        engine.Get("s1")!.State.ShouldBe(SessionState.Waiting);
        engine.Get("s1")!.JobsEndedAt.ShouldBe(ended);

        _time.Advance(TimeSpan.FromSeconds(30));
        engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: "a1"));
        engine.Get("s1")!.State.ShouldBe(SessionState.Working, "a turn that reacts to the end may still come");
        engine.SweepStale();
        engine.Get("s1")!.State.ShouldBe(SessionState.Working, "the wait begins at the answer, not at the stored end");

        _time.Advance(SessionEngine.JobEndSettle);
        engine.SweepStale();
        engine.Get("s1")!.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void A_transcript_write_after_a_restart_does_not_run_out_the_stored_wait_at_once()
    {
        // Review round 1 of #395: with no hook yet, a transcript write moves the restored Waiting chat to Working, and the wait
        // kept its stored end, so the next sweep made the chat Idle while the prompt may still be open.
        var ended = _time.GetUtcNow().AddMinutes(-5);
        using var engine = Restored(Stored() with { State = SessionState.Waiting, JobsEndedAt = ended, AwaitingToolResult = true }, new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

        engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(), InferredSignal = SessionSignal.ToolUse,
        });
        engine.Get("s1")!.State.ShouldBe(SessionState.Working);
        engine.Get("s1")!.JobsEndedAt.ShouldBe(_time.GetUtcNow(), "Waiting to Working is the prompt answered: the wait begins now");

        engine.SweepStale();
        engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_reacting_request_read_with_the_answer_after_a_restart_ends_the_wait()
    {
        // Review round 2 of #395: the wait began anew at the write before the request was weighed, so the request, older than
        // the write, no longer ended it, and the sweep made the chat Idle in the middle of its reacting turn.
        using var engine = Restored(Stored() with { State = SessionState.Waiting, JobsEndedAt = _time.GetUtcNow().AddMinutes(-5), AwaitingToolResult = true },
            new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

        engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(),
            LastRequestAt = _time.GetUtcNow().AddSeconds(-1), InferredSignal = SessionSignal.ToolUse,
        });

        engine.Get("s1")!.JobsEndedAt.ShouldBeNull("the turn that reacts has begun, and ends with its own stop");
    }

    [Fact]
    public void A_chat_restored_working_restores_as_before_the_wait_was_stored()
    {
        // Review round 3 of #395: a wait that ran out while the app was down ended a chat restored mid-command at the first
        // sweep, before the transcript could tell the reacting turn that began meanwhile. Only a Waiting chat needs the wait.
        using var engine = Restored(Stored() with { JobsEndedAt = _time.GetUtcNow().AddMinutes(-10), AwaitingToolResult = true },
            new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));
        engine.Get("s1")!.JobsEndedAt.ShouldBeNull();

        _time.Advance(TimeSpan.FromSeconds(5));
        engine.SweepStale();
        engine.Get("s1")!.State.ShouldBe(SessionState.Working, "a command still runs, as far as the app can tell");
    }

    private SessionEngine RestoredWaitingOn(string agents, bool turnEnded = false) => Restored(
        Stored() with { State = SessionState.Waiting, StateSince = _time.GetUtcNow().AddMinutes(-5), WaitingAgents = agents, WaitingTurnEnded = turnEnded, AwaitingToolResult = true },
        new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

    private TranscriptUpdate WrittenBy(string? agent, DateTimeOffset at) => new()
    {
        SessionId = "s1", TranscriptPath = agent is null ? "p" : $"p/subagents/agent-{agent}.jsonl", ObservedAt = _time.GetUtcNow(),
        LastActivityAt = at, AgentId = agent, InferredSignal = _time.GetUtcNow() - at < TimeSpan.FromSeconds(10) ? SessionSignal.ToolUse : null,
    };

    [Fact]
    public void A_chat_tells_whom_it_waits_for()
    {
        Prompt();
        AgentAsks("a1");
        Chat.WaitingAgents.ShouldBe("a1");
        _engine.Apply(Hook("PermissionRequest", SessionSignal.Notification));
        Chat.WaitingAgents.ShouldBe("a1\nmain");

        Stop();
        Chat.WaitingTurnEnded.ShouldBeTrue("the main turn ended while a1's prompt waits");
        Chat.WaitingAgents.ShouldBe("a1");

        AgentSteps("a1");
        Chat.State.ShouldBe(SessionState.Idle);
        Chat.WaitingAgents.ShouldBeNull();
        Chat.WaitingTurnEnded.ShouldBeFalse();
    }

    [Fact]
    public void After_a_restart_another_agent_s_step_leaves_the_chat_waiting()
    {
        // #401: the first tool use of any agent ended a restored Waiting, so the chat could show Idle while a1's prompt waited.
        using var engine = RestoredWaitingOn("a1");

        engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: "a2"));
        engine.Get("s1")!.State.ShouldBe(SessionState.Waiting, "a1's prompt still waits for the user");

        engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: "a1"));
        engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void After_a_restart_a_turn_that_ended_while_the_prompt_waited_is_over_once_it_is_answered()
    {
        using var engine = RestoredWaitingOn("a1", turnEnded: true);

        engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: "a1"));

        engine.Get("s1")!.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void After_a_restart_the_main_agent_s_prompt_is_answered_by_its_own_step_only()
    {
        using var engine = RestoredWaitingOn("main");

        engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: "a2"));
        engine.Get("s1")!.State.ShouldBe(SessionState.Waiting);

        engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse));
        engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_write_of_the_waiting_agent_after_its_prompt_ends_the_wait()
    {
        // #401: a prompt answered while the app was down; no hook of that agent comes again, but its transcript tells it.
        using var engine = RestoredWaitingOn("a1");

        engine.Apply(WrittenBy("a1", _time.GetUtcNow().AddMinutes(-2)));

        engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_write_of_another_agent_leaves_a_restored_chat_waiting()
    {
        using var engine = RestoredWaitingOn("a1");

        engine.Apply(WrittenBy("a2", _time.GetUtcNow()));
        engine.Apply(WrittenBy(null, _time.GetUtcNow()));

        engine.Get("s1")!.State.ShouldBe(SessionState.Waiting);
    }

    [Fact]
    public void A_write_of_the_waiting_agent_from_before_its_prompt_leaves_the_chat_waiting()
    {
        using var engine = RestoredWaitingOn("a1");

        engine.Apply(WrittenBy("a1", _time.GetUtcNow().AddMinutes(-6)));

        engine.Get("s1")!.State.ShouldBe(SessionState.Waiting, "the line that asked was written before the prompt");
    }

    [Fact]
    public void A_chat_restored_waiting_with_no_one_named_ends_at_the_first_step_as_before()
    {
        using var engine = Restored(Stored() with { State = SessionState.Waiting }, new FakeProcessProbe().Run(77, _time.GetUtcNow().AddHours(-1)));

        engine.Apply(Hook("PostToolUse", SessionSignal.ToolUse, agent: "a2"));

        engine.Get("s1")!.State.ShouldBe(SessionState.Working, "a row stored before #401 names no one");
    }

    [Fact]
    public void A_chat_restored_idle_keeps_no_wait()
    {
        using var engine = Restored(Stored() with { JobsEndedAt = _time.GetUtcNow().AddMinutes(-5) }, new FakeProcessProbe());

        engine.Get("s1")!.State.ShouldBe(SessionState.Idle);
        engine.Get("s1")!.JobsEndedAt.ShouldBeNull("a chat that is done waits for nothing");
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
