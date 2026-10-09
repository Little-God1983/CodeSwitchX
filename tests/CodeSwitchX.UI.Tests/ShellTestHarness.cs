using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Data;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Voice;
using CodeSwitchX.UI.Yard;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests;

/// <summary>Builds a ShellViewModel with substituted stores and Win32 seams; everything else is the real code.</summary>
public sealed class ShellTestHarness
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    public EventBus Bus { get; } = new(NullLogger<EventBus>.Instance);
    public IWorkspaceStore Workspaces { get; } = Substitute.For<IWorkspaceStore>();
    public IUsageStore Usage { get; } = Substitute.For<IUsageStore>();
    public ISettingsStore Settings { get; } = Substitute.For<ISettingsStore>();
    internal Raven.FakeOpenMic OpenMic { get; } = new();
    public IWindowEnumerator Windows { get; } = Substitute.For<IWindowEnumerator>();
    public IWindowDocker Docker { get; } = Substitute.For<IWindowDocker>();
    public IVsCodeLauncher Launcher { get; } = Substitute.For<IVsCodeLauncher>();

    public ChatSettings Chats { get; } = new();
    public IMicrophoneCatalog Microphones { get; } = Substitute.For<IMicrophoneCatalog>();
    public ISpeakerCatalog Speakers { get; } = Substitute.For<ISpeakerCatalog>();
    public IAudioOutput AudioOutput { get; } = Substitute.For<IAudioOutput>();
    public IMicrophoneRecorder Recorder { get; } = Substitute.For<IMicrophoneRecorder>();
    public IDictationService Dictation { get; } = Substitute.For<IDictationService>();
    internal Raven.FakeSpeech Voice { get; } = new();
    public IWhisperModelStore Models { get; } = Substitute.For<IWhisperModelStore>();
    public SpeechSettings Speech { get; } = new();
    internal Voice.FakeEngineVoice Kokoro { get; } = new(SpeechEngine.Kokoro);
    internal Voice.FakeEngineVoice Qwen { get; } = new(SpeechEngine.Qwen);
    public SpeechEngines Engines { get; }
    public VoiceStatusViewModel VoiceStatus { get; }
    internal Settings.FakeVoiceSamples Samples { get; } = new();
    public WorkspaceResolver Resolver { get; } = new();
    public SessionEngine Engine { get; }
    public HostManager Host { get; }
    public ShellViewModel Shell { get; }
    /// <summary>Its id is the one the fake Yard directory gives "App", so a card of a chat there is placed in its Raven chat.</summary>
    public Workspace App { get; } = new() { Id = Raven.FakeYardDirectory.WorkspaceOf("App"), Name = "App", RootPath = @"c:\repo\app" };

    /// <summary>What chats ask, held for Raven's panel.</summary>
    public CodeSwitchX.Core.Sessions.ChatAsks Asks { get; }

    /// <summary>The Yard as Raven's tools see it: the chats that ask, and where.</summary>
    internal Raven.FakeYardDirectory YardDirectory { get; } = new();
    public Track General { get; } = new() { Name = "General" };

    public ShellTestHarness()
    {
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        App.TrackId = General.Id;
        Workspaces.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([General]));
        Workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([App]));
        Usage.GetBucketsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<UsageBucket>>([]));
        Settings.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PricingRule>>([]));
        Windows.ProcessName(Arg.Any<uint>()).Returns("Code");
        Docker.IsAlive(Arg.Any<nint>()).Returns(true);

        Engine = new SessionEngine(Bus, Resolver, Time, NullLogger<SessionEngine>.Instance);
        Host = new HostManager(Windows, Docker, Launcher, Bus, TimeProvider.System, NullLogger<HostManager>.Instance,
            new HostManagerOptions { DiscoveryTimeout = TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromMilliseconds(5) });

        var telemetry = new TelemetryService(Usage, Settings, Bus, Time, NullLogger<TelemetryService>.Instance);
        var registry = new WorkspaceRegistry(Workspaces, Resolver, Bus);
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));
        var dispatcher = new ImmediateDispatcher();
        var yard = new YardViewModel(Workspaces, registry, Engine, telemetry, git, Bus, dispatcher, Time, NullLogger<YardViewModel>.Instance);
        var cab = new CabViewModel();
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "csx-shell-" + Guid.NewGuid().ToString("N")));
        var claude = new ClaudeCodePaths(Path.Combine(paths.Root, "home"));
        Engines = new SpeechEngines(Speech, [Kokoro, Qwen]);
        VoiceStatus = new VoiceStatusViewModel(Engines, Speech, Dictation, dispatcher);
        var settings = new SettingsViewModel(new ClaudeHookInstaller(claude, NullLogger<ClaudeHookInstaller>.Instance), Settings, new PersistenceWriterOptions(), new BrainSettings(), Chats,
            Speech, Engines, Models, VoiceStatus, paths, claude, Samples, dispatcher, NullLogger<SettingsViewModel>.Instance);
        var bar = new PerformanceBarViewModel(telemetry, Engine, Bus, dispatcher, Settings, Time, VoiceStatus);
        Microphones.List().Returns([]);
        Speakers.List().Returns([]);
        var raven = new RavenPanelViewModel(Microphones, Recorder, Dictation, Models,
            Substitute.For<IDictationVocabularyProvider>(), new Raven.FakeBrain(), Voice.NewVoice(), Voice, dispatcher, Time, NullLogger<RavenPanelViewModel>.Instance, openMic: OpenMic,
            asks: Asks = new CodeSwitchX.Core.Sessions.ChatAsks(Bus, Time) { Takes = _ => true }, yard: YardDirectory,
            speakers: new SpeakerChoice(Speakers, AudioOutput, dispatcher, Time, NullLogger<SpeakerChoice>.Instance));
        Shell = new ShellViewModel(yard, cab, settings, bar, raven, Chats, Host, NullLogger<ShellViewModel>.Instance, VsCode);
    }

    /// <summary>The chats the shell asked VS Code to show (#115).</summary>
    public ShownChats VsCode { get; } = new();

    public sealed class ShownChats : IVsCodeChats
    {
        public List<(string Workspace, string SessionId)> Shown { get; } = [];

        /// <summary>Why VS Code does not show the chat; null when it does.</summary>
        public string? Failure { get; set; }

        public Task<VsCodeChat> StartAsync(Workspace workspace, string? folder, string? model, string? effort, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CloseAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> CompactAsync(string sessionId, string folder, string title, string? keep, Action? tabClosed, CancellationToken ct) =>
            throw new NotSupportedException();

        /// <summary>When set, a show waits until it is ended: VS Code is slow to start.</summary>
        public bool Hangs { get; set; }

        /// <summary>The shows that were ended while they waited.</summary>
        public int Ended;

        public async Task ShowAsync(Workspace workspace, string sessionId, CancellationToken ct)
        {
            Shown.Add((workspace.Name, sessionId));
            if (Hangs)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Ended);
                    throw;
                }
            }

            if (Failure is { } failure)
            {
                throw new CodeSwitchX.Core.Yard.YardActionException(failure);
            }
        }
    }

    public static YardViewModel CreateYardWithoutInit() => new ShellTestHarness().Shell.Yard;

    public void VsCodeWindowAppears(nint hwnd = 500)
    {
        Windows.TopLevelWindows().Returns([], [new WindowInfo(hwnd, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")]);
        Launcher.Launch(Arg.Any<Workspace>()).Returns(new LaunchResult(true, 1, null));
    }
}
