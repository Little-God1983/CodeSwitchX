using System.Diagnostics;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class ProcessLivenessMonitorTests
{
    [Fact]
    public void Tick_marks_sessions_whose_claude_process_is_gone()
    {
        var time = new FakeTimeProvider();
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var engine = new SessionEngine(bus, new WorkspaceResolver(), time, NullLogger<SessionEngine>.Instance);
        engine.Apply(new HookEvent
        {
            SessionId = "alive", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = time.GetUtcNow(),
            ParentChain = [new ProcessRef(10, "claude.exe")],
        });
        engine.Apply(new HookEvent
        {
            SessionId = "dead", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = time.GetUtcNow(),
            ParentChain = [new ProcessRef(20, "claude.exe")],
        });
        var probe = Substitute.For<IProcessProbe>();
        probe.IsAlive(10, Arg.Any<DateTimeOffset>()).Returns(true);
        probe.IsAlive(20, Arg.Any<DateTimeOffset>()).Returns(false);
        var monitor = new ProcessLivenessMonitor(engine, probe, time, NullLogger<ProcessLivenessMonitor>.Instance);

        monitor.Tick();

        engine.Get("alive")!.State.ShouldBe(SessionState.Working);
        engine.Get("dead")!.State.ShouldBe(SessionState.Errored);
    }

    [Fact]
    public void SystemProcessProbe_reports_the_current_process_alive_and_a_bogus_pid_dead()
    {
        var probe = new SystemProcessProbe();

        probe.IsAlive(Environment.ProcessId, DateTimeOffset.UtcNow).ShouldBeTrue();
        probe.IsAlive(int.MaxValue - 1, DateTimeOffset.UtcNow).ShouldBeFalse();
    }

    [Fact]
    public void Tick_survives_a_probe_that_throws_and_still_checks_the_other_sessions()
    {
        var time = new FakeTimeProvider();
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var engine = new SessionEngine(bus, new WorkspaceResolver(), time, NullLogger<SessionEngine>.Instance);
        engine.Apply(new HookEvent
        {
            SessionId = "protected", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = time.GetUtcNow(),
            ParentChain = [new ProcessRef(10, "claude.exe")],
        });
        engine.Apply(new HookEvent
        {
            SessionId = "dead", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = time.GetUtcNow(),
            ParentChain = [new ProcessRef(20, "claude.exe")],
        });
        var probe = Substitute.For<IProcessProbe>();
        probe.IsAlive(10, Arg.Any<DateTimeOffset>()).Throws(new System.ComponentModel.Win32Exception(5, "Access is denied"));
        probe.IsAlive(20, Arg.Any<DateTimeOffset>()).Returns(false);
        var monitor = new ProcessLivenessMonitor(engine, probe, time, NullLogger<ProcessLivenessMonitor>.Instance);

        Should.NotThrow(() => monitor.Tick());

        engine.Get("protected")!.State.ShouldBe(SessionState.Working, "a PID we cannot query is not evidence that the chat died");
        engine.Get("dead")!.State.ShouldBe(SessionState.Errored);
    }

    [Fact]
    public void SystemProcessProbe_treats_a_process_it_may_not_query_as_alive()
    {
        // PID 4 is the Windows System process: it exists, but HasExited needs SYNCHRONIZE access a normal user lacks.
        new SystemProcessProbe().IsAlive(4, DateTimeOffset.UtcNow).ShouldBeTrue();
    }

    [Fact]
    public void Tick_marks_a_session_errored_when_its_pid_now_belongs_to_a_process_started_after_the_chat_was_seen()
    {
        var time = new FakeTimeProvider();
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var engine = new SessionEngine(bus, new WorkspaceResolver(), time, NullLogger<SessionEngine>.Instance);
        engine.Apply(new HookEvent
        {
            SessionId = "reused", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = time.GetUtcNow(),
            ParentChain = [new ProcessRef(30, "claude.exe")],
        });
        var claudeExitedAndPidWasReusedAt = time.GetUtcNow().AddMinutes(30);
        time.Advance(TimeSpan.FromHours(1));
        var probe = new FakeProcessProbe().Run(30, claudeExitedAndPidWasReusedAt);
        var monitor = new ProcessLivenessMonitor(engine, probe, time, NullLogger<ProcessLivenessMonitor>.Instance);

        monitor.Tick();

        engine.Get("reused")!.State.ShouldBe(SessionState.Errored, "Windows reuses process ids; a process that started after the chat's last event is not its claude");
    }

    [Fact]
    public void SystemProcessProbe_does_not_take_a_process_that_started_after_the_chat_was_seen_for_the_chats_process()
    {
        using var current = Process.GetCurrentProcess();
        var beforeThisProcessStarted = new DateTimeOffset(current.StartTime.ToUniversalTime()).AddMinutes(-1);

        new SystemProcessProbe().IsAlive(Environment.ProcessId, beforeThisProcessStarted).ShouldBeFalse();
    }
}
