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
        probe.IsAlive(10).Returns(true);
        probe.IsAlive(20).Returns(false);
        var monitor = new ProcessLivenessMonitor(engine, probe, time, NullLogger<ProcessLivenessMonitor>.Instance);

        monitor.Tick();

        engine.Get("alive")!.State.ShouldBe(SessionState.Working);
        engine.Get("dead")!.State.ShouldBe(SessionState.Errored);
    }

    [Fact]
    public void SystemProcessProbe_reports_the_current_process_alive_and_a_bogus_pid_dead()
    {
        var probe = new SystemProcessProbe();

        probe.IsAlive(Environment.ProcessId).ShouldBeTrue();
        probe.IsAlive(int.MaxValue - 1).ShouldBeFalse();
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
        probe.IsAlive(10).Throws(new System.ComponentModel.Win32Exception(5, "Access is denied"));
        probe.IsAlive(20).Returns(false);
        var monitor = new ProcessLivenessMonitor(engine, probe, time, NullLogger<ProcessLivenessMonitor>.Instance);

        Should.NotThrow(() => monitor.Tick());

        engine.Get("protected")!.State.ShouldBe(SessionState.Working, "a PID we cannot query is not evidence that the chat died");
        engine.Get("dead")!.State.ShouldBe(SessionState.Errored);
    }

    [Fact]
    public void SystemProcessProbe_treats_a_process_it_may_not_query_as_alive()
    {
        // PID 4 is the Windows System process: it exists, but HasExited needs SYNCHRONIZE access a normal user lacks.
        new SystemProcessProbe().IsAlive(4).ShouldBeTrue();
    }
}
