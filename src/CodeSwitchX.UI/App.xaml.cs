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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

        try
        {
            var builder = Host.CreateApplicationBuilder();
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
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CodeSwitchX failed to start");
            MessageBox.Show($"CodeSwitchX failed to start:\n\n{ex.Message}\n\nSee {paths.LogsDirectory}", "CodeSwitchX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
        services.AddHostedService<ProcessLivenessMonitor>();

        services.AddCodeSwitchXData(paths.DatabaseFile);
        services.AddCodeSwitchXIngest();
        services.AddCodeSwitchXTelemetry();
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
