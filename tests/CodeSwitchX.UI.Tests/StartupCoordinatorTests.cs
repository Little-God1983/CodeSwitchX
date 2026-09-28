using System.Text.Json;
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
using NSubstitute.ExceptionExtensions;

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
        Restoring();
        _writer = new PersistenceWriter(_bus, _sessions, Substitute.For<IUsageStore>(), _time, NullLogger<PersistenceWriter>.Instance, _writerOptions);
        _engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance);
    }

    private StartupCoordinator Coordinator() =>
        new(new WorkspaceRegistry(_workspaces, _resolver, _bus), _sessions, _settings, _engine, _writerOptions, _time, NullLogger<StartupCoordinator>.Instance);

    private void Restoring(params SessionRecord[] records) =>
        _sessions.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionRecord>>(records));

    private SessionRecord Stored(string id, SessionState state, TimeSpan quietFor, bool resolved = false) => SessionRecord.FromSnapshot(new SessionSnapshot
    {
        SessionId = id, State = state, StartedAt = _time.GetUtcNow().AddHours(-2), LastEventAt = _time.GetUtcNow() - quietFor,
        StateSince = _time.GetUtcNow() - quietFor, Cwd = @"c:\repo\app\src", WorkspaceId = resolved ? _workspace.Id : null,
    });

    private HookEvent SessionStart(string id) => new() { SessionId = id, EventName = "SessionStart", Signal = SessionSignal.SessionStart, At = _time.GetUtcNow() };

    [Fact]
    public async Task Start_loads_workspaces_restores_recent_sessions_and_starts_the_engine()
    {
        Restoring(Stored("s1", SessionState.Idle, TimeSpan.FromHours(1)));

        await Coordinator().StartAsync(CancellationToken.None);

        await _sessions.Received(1).GetActiveSinceAsync(_time.GetUtcNow().AddHours(-24), Arg.Any<CancellationToken>());
        _engine.Get("s1")!.WorkspaceId.ShouldBe(_workspace.Id);
        _resolver.Resolve(@"c:\repo\app").ShouldBe(_workspace.Id);

        _bus.Publish(new HookEventReceived(SessionStart("s2")));
        _engine.Get("s2").ShouldNotBeNull("Start() must have subscribed the engine to the bus");
    }

    /// <summary>
    /// The host starts the writer before the coordinator (AppHostTests pins that order), so every change the start makes
    /// to a restored chat reaches its stored row: the workspace re-resolve gives it, and the state restore corrects.
    /// </summary>
    [Fact]
    public async Task What_the_start_changes_on_a_restored_chat_is_published_for_the_listening_writer()
    {
        _writer.Subscribe();
        Restoring(
            Stored("moved", SessionState.Idle, TimeSpan.FromHours(1)),
            Stored("quiet", SessionState.Working, TimeSpan.FromMinutes(3), resolved: true),
            Stored("same", SessionState.Idle, TimeSpan.FromHours(1), resolved: true));

        await Coordinator().StartAsync(CancellationToken.None);

        _engine.Get("moved")!.WorkspaceId.ShouldBe(_workspace.Id);
        _engine.Get("quiet")!.State.ShouldBe(SessionState.Idle);
        _writer.Pending.ShouldBe(2, "re-resolve placed one chat and restore demoted another; the third came back as stored");
    }

    /// <summary>
    /// "Store raw hook payloads" is opt-in and stored; the Event API opens the pipe right after the coordinator, so the
    /// writer needs the choice from here or the first events are saved without their payload.
    /// </summary>
    [Fact]
    public async Task Start_gives_the_writer_the_stored_payload_choice()
    {
        _settings.GetAsync<bool?>(SettingKeys.StorePayloads, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));

        await Coordinator().StartAsync(CancellationToken.None);

        _writerOptions.StorePayloads.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stored_payload_choice_that_cannot_be_read_leaves_payloads_off_and_does_not_stop_the_start()
    {
        _settings.GetAsync<bool?>(SettingKeys.StorePayloads, Arg.Any<CancellationToken>()).ThrowsAsync(new JsonException("\"true\" is not a JSON bool"));

        await Coordinator().StartAsync(CancellationToken.None);

        _writerOptions.StorePayloads.ShouldBeFalse("off is the privacy-safe side");
        _bus.Publish(new HookEventReceived(SessionStart("s2")));
        _engine.Get("s2").ShouldNotBeNull("the start went on");
    }
}
