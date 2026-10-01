using System.Windows;
using System.Windows.Threading;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Data;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Ingest;
using CodeSwitchX.Ingest.Transcripts;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Workspaces;
using CodeSwitchX.UI.Yard;
using CodeSwitchX.Voice;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Extensions.Logging;

namespace CodeSwitchX.UI;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _instance;
    private IDisposable? _crashLogging;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Before the log, the database or the pipe is opened: a second instance brings the running one forward and ends.
        var claim = SingleInstance.Claim(SingleInstance.DefaultName, SingleInstance.AnswerTimeout);
        if (claim.Result is not (ClaimResult.Claimed or ClaimResult.Unavailable))
        {
            if (AnotherInstanceMessage(claim.Result) is { } message)
            {
                MessageBox.Show(message, "CodeSwitchX", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _instance = claim.Instance;
        var paths = AppPaths.Default();
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            // Before the log exists and before any handler is attached, a message box is the only place this can be said;
            // without it CodeSwitchX just ended. Whatever the cause (a redirected, malformed LOCALAPPDATA included), all
            // that is done with it is to say so and exit.
            MessageBox.Show($"CodeSwitchX cannot create its data folder:\n\n{paths.Root}\n\n{ex.Message}", "CodeSwitchX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "codeswitchx-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.Debug()
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _crashLogging = UnhandledExceptionLogging.Attach(new SerilogLoggerFactory(Log.Logger).CreateLogger<App>());
        if (claim.Problem is { } problem)
        {
            Log.Warning(problem, "Could not check whether CodeSwitchX is already running; starting without that check");
        }

        try
        {
            _host = CreateHostBuilder(paths).Build();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseHostedWindows();

            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
            await _host.StartAsync();

            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync(CancellationToken.None);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            _instance?.OnActivationRequested(() => BringForward(window));
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CodeSwitchX failed to start");
            MessageBox.Show($"CodeSwitchX failed to start:\n\n{ex.Message}\n\nSee {paths.LogsDirectory}", "CodeSwitchX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string? AnotherInstanceMessage(ClaimResult result) => result switch
    {
        ClaimResult.NoAnswer => "CodeSwitchX is already running, but its window did not come forward. It may still be starting "
            + "or closing. If it does not appear, end CodeSwitchX.exe in Task Manager and start it again.",
        ClaimResult.InAnotherSession => "CodeSwitchX is already running in another Windows session of this account. Close it there first.",
        _ => null,
    };

    /// <summary>For a later start: true once the window has come forward on the UI thread, which a hung shell never does.</summary>
    private bool BringForward(Window window)
    {
        var done = false;
        Dispatcher.Invoke(() =>
        {
            WindowActivation.BringUp(window);
            done = true;
        }, DispatcherPriority.Normal, CancellationToken.None, SingleInstance.AnswerTimeout);
        return done;
    }

    /// <summary>
    /// The app's host, with every service registered, logging through Serilog. It reads no configuration: the defaults
    /// take the current directory as the content root and parse the appsettings.json there and the environment, and
    /// started from an ASP.NET Core project's terminal, a malformed file in that project failed the start. Nothing in
    /// CodeSwitchX reads configuration.
    /// </summary>
    internal static HostApplicationBuilder CreateHostBuilder(AppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger);
        ConfigureServices(builder.Services, paths);
        return builder;
    }

    private static void ConfigureServices(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(ClaudeCodePaths.Default());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Current.Dispatcher));

        services.AddSingleton<WorkspaceResolver>();
        services.AddSingleton<IWorkspaceResolver>(sp => sp.GetRequiredService<WorkspaceResolver>());
        services.AddSingleton<WorkspaceRegistry>();
        services.AddSingleton<GitInspector>();
        services.AddSingleton<WorkspaceProbe>();
        services.AddSingleton<SessionEngineOptions>();
        services.AddSingleton<SessionEngine>();
        services.AddSingleton<IProcessProbe, SystemProcessProbe>();

        // Hosted services start in registration order, and the flows depend on it (AppHostTests pins the pairs that
        // matter): the writer listens before the coordinator starts the engine, so what restore, re-resolve and the first
        // sweep change is saved; the engine listens before the pipe opens and before the first scan; the pipe opens before
        // telemetry loads seven days of usage, so a hook fired during the start does not wait for that; and telemetry
        // listens before the indexer's first scan, so the usage that scan finds reaches it.
        services.AddCodeSwitchXData(paths.DatabaseFile);
        services.AddHostedService<StartupCoordinator>();
        services.AddHostedService<ProcessLivenessMonitor>();
        services.AddCodeSwitchXEventApi();
        services.AddCodeSwitchXTelemetry();
        services.AddCodeSwitchXTranscriptIndexer();
        services.AddCodeSwitchXHosting();
        services.AddCodeSwitchXVoice(paths.ModelsDirectory, paths.VoiceDirectory);
        services.AddSingleton<IDictationVocabularyProvider>(sp =>
            new WorkspaceVocabularyProvider(sp.GetRequiredService<IWorkspaceStore>(), WorkspaceProbe.FoldersOf,
                sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<TimeProvider>()));

        // Raven's brain, and the Yard it looks at through the MCP tools the Event API serves.
        services.AddSingleton<BrainSettings>();
        services.AddSingleton<IBrainProcessLauncher, BrainProcessLauncher>();
        services.AddSingleton<IConductorBrain>(sp => new ClaudeCliBrain(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<BrainSettings>(),
            sp.GetRequiredService<IBrainProcessLauncher>(), () => ClaudeCliLocator.Default().Find(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ClaudeCliBrain>>()));
        // The teller words chat news with no tools and a conversation of its own: what other chats said never reaches the
        // brain that acts.
        services.AddKeyedSingleton<IConductorBrain>(RavenPanelViewModel.TellerKey, (sp, _) => new ClaudeCliBrain(sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<BrainSettings>(), sp.GetRequiredService<IBrainProcessLauncher>(), () => ClaudeCliLocator.Default().Find(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ClaudeCliBrain>>(), BrainRole.Teller));
        services.AddSingleton<IYardDirectory>(sp => new YardDirectory(sp.GetRequiredService<YardViewModel>(), sp.GetRequiredService<SessionEngine>().Get,
            sp.GetRequiredService<IUiDispatcher>(), WorkspaceProbe.FoldersOf, id => sp.GetRequiredService<IAgentLauncher>().Find(id) is not null));

        // The chats Raven starts, and what else it does on the Yard through the MCP tools. The shell is asked for when an
        // action first needs it: the Event API that serves the tools starts before the window is made.
        services.AddSingleton<ChatSettings>();
        services.AddSingleton<IAgentLauncher>(sp => new ClaudeAgentLauncher(sp.GetRequiredService<IBrainProcessLauncher>(),
            () => ClaudeCliLocator.Default().Find(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ClaudeAgentLauncher>>()));
        services.AddSingleton<IYardActions>(sp => new RavenActions(sp.GetRequiredService<IAgentLauncher>(), sp.GetRequiredService<ChatSettings>(),
            sp.GetRequiredService<SessionEngine>().Claim, () => sp.GetRequiredService<ShellViewModel>(), sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<IVsCodeLauncher>().OpenUrl,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<RavenActions>>()));

        services.AddSingleton<YardViewModel>();
        services.AddSingleton<CabViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PerformanceBarViewModel>();
        services.AddSingleton(sp => new ChatNews(sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<IYardDirectory>(),
            sp.GetRequiredService<TimeProvider>(), path => TranscriptLastReply.Read(path)));
        services.AddSingleton<RavenPanelViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<AddWorkspaceViewModel>();
        services.AddSingleton<Func<AddWorkspaceViewModel>>(sp => () => sp.GetRequiredService<AddWorkspaceViewModel>());
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Synchronous on purpose: an async-void OnExit yields to WPF, which tears the dispatcher down and drops the
        // continuation, so endpoint.json would never be deleted and the last persistence batch never flushed.
        if (_host is not null)
        {
            try
            {
                ReleaseHostedWindows();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Task.Run(async () =>
                {
                    await FlushSettingsSavesAsync(cts.Token);
                    await _host.StopAsync(cts.Token);
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Host shutdown did not complete cleanly");
            }

            _host.Dispose();
            // ProcessExit runs after this on every exit and releases the hosted windows through the host; a disposed one throws.
            _host = null;
        }

        _crashLogging?.Dispose();
        Log.CloseAndFlush();
        // Last: until the host has stopped, this process still holds the pipe and the database.
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A setting changed right before the exit is still on the Settings view's save queue; it must reach the database before the host stops.</summary>
    private async Task FlushSettingsSavesAsync(CancellationToken ct)
    {
        try
        {
            if (_host?.Services.GetService<SettingsViewModel>() is { } settings)
            {
                await settings.FlushSavesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Settings saves did not finish before exit");
        }
    }

    private void ReleaseHostedWindows()
    {
        try
        {
            _host?.Services.GetService<HostManager>()?.ReleaseAll();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Releasing hosted windows failed");
        }
    }
}
