using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Data;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests;

public class StartupCoordinatorTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly WorkspaceResolver _resolver = new();
    private readonly IWorkspaceStore _workspaces = Substitute.For<IWorkspaceStore>();
    private readonly ISessionStore _sessions = Substitute.For<ISessionStore>();
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();
    private readonly PersistenceWriterOptions _writerOptions = new();
    private readonly PersistenceWriter _writer;
    private readonly SessionEngine _engine;
    private readonly Workspace _workspace = new() { Name = "App", RootPath = @"c:\repo\app" };

    public StartupCoordinatorTests()
    {
        _workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_workspace]));
        _sessions.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionRecord>>([]));
        _writer = new PersistenceWriter(_bus, _sessions, Substitute.For<IUsageStore>(), _time, NullLogger<PersistenceWriter>.Instance, _writerOptions);
        _engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance);
    }

    private StartupCoordinator Coordinator() =>
        new(new WorkspaceRegistry(_workspaces, _resolver, _bus), _sessions, _settings, _engine, _writer, _writerOptions, _time, NullLogger<StartupCoordinator>.Instance);

    private SessionRecord Idle(string id, TimeSpan quietFor, bool resolved = false) => SessionRecord.FromSnapshot(new SessionSnapshot
    {
        SessionId = id, State = SessionState.Idle, StartedAt = _time.GetUtcNow().AddHours(-2), LastEventAt = _time.GetUtcNow() - quietFor,
        StateSince = _time.GetUtcNow() - quietFor, Cwd = @"c:\repo\app\src", WorkspaceId = resolved ? _workspace.Id : null,
    });

    [Fact]
    public async Task Run_loads_workspaces_restores_recent_sessions_and_starts_the_engine()
    {
        _sessions.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SessionRecord>>([Idle("s1", TimeSpan.FromHours(1))]));

        await Coordinator().RunAsync(CancellationToken.None);

        await _sessions.Received(1).GetActiveSinceAsync(_time.GetUtcNow().AddHours(-24), Arg.Any<CancellationToken>());
        _engine.Get("s1")!.WorkspaceId.ShouldBe(_workspace.Id);
        _resolver.Resolve(@"c:\repo\app").ShouldBe(_workspace.Id);

        _bus.Publish(new HookEventReceived(new HookEvent { SessionId = "s2", EventName = "SessionStart", Signal = SessionSignal.SessionStart, At = _time.GetUtcNow() }));
        _engine.Get("s2").ShouldNotBeNull("Start() must have subscribed the engine to the bus");
    }

    /// <summary>
    /// The engine's sweep timer runs from Start(), before the host starts the writer. A chat restored Idle and quiet for
    /// longer than the stale window is swept within the first seconds; without a writer listening, the stored row stays
    /// Idle while the engine says Stale.
    /// </summary>
    [Fact]
    public async Task A_chat_the_first_sweep_moves_is_saved_even_before_the_host_has_started_the_writer()
    {
        _sessions.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SessionRecord>>([Idle("s1", TimeSpan.FromMinutes(31), resolved: true)]));

        await Coordinator().RunAsync(CancellationToken.None);
        _time.Advance(new SessionEngineOptions().SweepInterval);

        _engine.Get("s1")!.State.ShouldBe(SessionState.Stale);
        _writer.Pending.ShouldBe(1, "the writer must be subscribed before the engine can publish");
    }

    /// <summary>
    /// "Store raw hook payloads" is opt-in and stored; the Event API listens from the host's start, before the shell reads
    /// its settings, so the writer needs the value before then or the first events are saved without their payload.
    /// </summary>
    [Fact]
    public async Task Run_gives_the_writer_the_stored_payload_setting_before_the_host_starts()
    {
        _settings.GetAsync<bool?>(SettingKeys.StorePayloads, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));

        await Coordinator().RunAsync(CancellationToken.None);

        _writerOptions.StorePayloads.ShouldBeTrue();
    }
}
