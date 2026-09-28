using System.Windows;
using System.Windows.Threading;
using CodeSwitchX.Core;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Data;
using CodeSwitchX.Hosting;
using CodeSwitchX.Ingest;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Workspaces;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodeSwitchX.UI;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _instance;

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
        paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "codeswitchx-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.Debug()
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        if (claim.Problem is { } problem)
        {
            Log.Warning(problem, "Could not check whether CodeSwitchX is already running; starting without that check");
        }

        try
        {
            var builder = CreateHostBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog(Log.Logger);
            ConfigureServices(builder.Services, paths);
            _host = builder.Build();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseHostedWindows();

            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
            await _host.Services.GetRequiredService<StartupCoordinator>().RunAsync(CancellationToken.None);
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
    /// A host that reads no configuration. The defaults take the current directory as the content root and parse the
    /// appsettings.json there and the environment: started from an ASP.NET Core project's terminal, a malformed file in
    /// that project failed the start. Nothing in CodeSwitchX reads configuration.
    /// </summary>
    internal static HostApplicationBuilder CreateHostBuilder() =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = AppContext.BaseDirectory });

    internal static void ConfigureServices(IServiceCollection services, AppPaths paths)
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
        services.AddHostedService<ProcessLivenessMonitor>();

        // Hosted services start in this order: the writer before the pipe opens, so the first hook events are saved, and
        // telemetry before the indexer, so the usage of the first scan reaches it (see AppHostTests).
        services.AddCodeSwitchXData(paths.DatabaseFile);
        services.AddCodeSwitchXTelemetry();
        services.AddCodeSwitchXIngest();
        services.AddCodeSwitchXHosting();

        services.AddSingleton<StartupCoordinator>();
        services.AddSingleton<YardViewModel>();
        services.AddSingleton<CabViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PerformanceBarViewModel>();
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
                Task.Run(() => _host.StopAsync(cts.Token)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Host shutdown did not complete cleanly");
            }

            _host.Dispose();
        }

        Log.CloseAndFlush();
        // Last: until the host has stopped, this process still holds the pipe and the database.
        _instance?.Dispose();
        base.OnExit(e);
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
