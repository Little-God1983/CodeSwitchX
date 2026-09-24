using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests;

public class StartupCoordinatorTests
{
    [Fact]
    public async Task Run_loads_workspaces_restores_recent_sessions_and_starts_the_engine()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var resolver = new WorkspaceResolver();
        var workspaces = Substitute.For<IWorkspaceStore>();
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app" };
        workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([workspace]));
        var sessions = Substitute.For<ISessionStore>();
        var record = SessionRecord.FromSnapshot(new SessionSnapshot
        {
            SessionId = "s1", State = SessionState.Idle, StartedAt = time.GetUtcNow().AddHours(-2), LastEventAt = time.GetUtcNow().AddHours(-1), StateSince = time.GetUtcNow().AddHours(-1), Cwd = @"c:\repo\app\src",
        });
        sessions.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionRecord>>([record]));
        var engine = new SessionEngine(bus, resolver, time, NullLogger<SessionEngine>.Instance);
        var coordinator = new StartupCoordinator(new WorkspaceRegistry(workspaces, resolver, bus), sessions, engine, time, NullLogger<StartupCoordinator>.Instance);

        await coordinator.RunAsync(CancellationToken.None);

        await sessions.Received(1).GetActiveSinceAsync(time.GetUtcNow().AddHours(-24), Arg.Any<CancellationToken>());
        engine.Get("s1")!.WorkspaceId.ShouldBe(workspace.Id);
        resolver.Resolve(@"c:\repo\app").ShouldBe(workspace.Id);

        bus.Publish(new HookEventReceived(new HookEvent { SessionId = "s2", EventName = "SessionStart", Signal = SessionSignal.SessionStart, At = time.GetUtcNow() }));
        engine.Get("s2").ShouldNotBeNull("Start() must have subscribed the engine to the bus");
    }
}
