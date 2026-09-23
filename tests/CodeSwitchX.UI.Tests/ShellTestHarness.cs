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
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Yard;
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
    public IWindowEnumerator Windows { get; } = Substitute.For<IWindowEnumerator>();
    public IWindowDocker Docker { get; } = Substitute.For<IWindowDocker>();
    public IVsCodeLauncher Launcher { get; } = Substitute.For<IVsCodeLauncher>();
    public WorkspaceResolver Resolver { get; } = new();
    public SessionEngine Engine { get; }
    public HostManager Host { get; }
    public ShellViewModel Shell { get; }
    public Workspace App { get; } = new() { Name = "App", RootPath = @"c:\repo\app" };
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
        var settings = new SettingsViewModel(new ClaudeHookInstaller(claude, NullLogger<ClaudeHookInstaller>.Instance), Settings, new PersistenceWriterOptions(), paths, claude, NullLogger<SettingsViewModel>.Instance);
        var bar = new PerformanceBarViewModel(telemetry, Engine, Bus, dispatcher, Settings);
        Shell = new ShellViewModel(yard, cab, settings, bar, Host, NullLogger<ShellViewModel>.Instance);
    }

    public static YardViewModel CreateYardWithoutInit() => new ShellTestHarness().Shell.Yard;

    public void VsCodeWindowAppears(nint hwnd = 500)
    {
        Windows.TopLevelWindows().Returns([], [new WindowInfo(hwnd, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")]);
        Launcher.Launch(Arg.Any<Workspace>()).Returns(new LaunchResult(true, 1, null));
    }
}
