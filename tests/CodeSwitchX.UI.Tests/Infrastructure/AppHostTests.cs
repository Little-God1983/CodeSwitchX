using CodeSwitchX.Core;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Api;
using CodeSwitchX.Ingest.Transcripts;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeSwitchX.UI.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    /// <summary>Tests that change the process's current directory, which every other test would see.</summary>
    public const string Name = "Current directory";
}

[Collection(CurrentDirectoryCollection.Name)]
public class AppHostTests
{
    [Fact]
    public void A_malformed_appsettings_json_in_the_folder_CodeSwitchX_is_started_from_does_not_stop_the_start()
    {
        var folder = Directory.CreateTempSubdirectory("csx-cwd-").FullName;
        File.WriteAllText(Path.Combine(folder, "appsettings.json"), """{ "Logging": """);
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = folder;
        try
        {
            using var host = App.CreateHostBuilder(TempPaths()).Build();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Hosted services start in registration order, which is spread over the registration calls, and the flows depend on a
    /// few pairs of it. Only those pairs are pinned, on the builder the app uses, so a hosted service no flow depends on
    /// can be registered anywhere.
    /// </summary>
    [Fact]
    public void Hosted_services_start_in_the_order_the_flows_depend_on()
    {
        using var host = Builder().Build();

        var order = host.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        StartsBefore<PersistenceWriter, StartupCoordinator>(order,
            "the writer must listen before the engine restores, re-resolves and sweeps, or what those change is never saved");
        StartsBefore<StartupCoordinator, EventApiService>(order,
            "the engine must listen before the pipe opens, or the first hook events are saved but never applied");
        StartsBefore<EventApiService, TelemetryService>(order,
            "telemetry's start loads seven days of usage; a hook fired while the pipe waits for that is lost");
        StartsBefore<TelemetryService, TranscriptIndexer>(order,
            "telemetry must listen before the first scan, or the usage that scan finds is in neither the database it loaded nor its memory until the next start");
    }

    /// <summary>A constructor parameter without a registration otherwise shows up only as "CodeSwitchX failed to start" at the next manual run.</summary>
    [Fact]
    public void Every_registered_service_can_be_built()
    {
        var builder = Builder();
        // Every CodeSwitchX service the root resolves; the pooled DbContext is scoped and is only ever made by its factory.
        var registered = builder.Services.Where(d => d.Lifetime != ServiceLifetime.Scoped).Select(d => d.ServiceType).Distinct()
            .Where(t => t.Namespace?.StartsWith("CodeSwitchX", StringComparison.Ordinal) == true).ToList();
        using var host = builder.Build();

        foreach (var type in registered.Where(t => t != typeof(MainWindow)))
        {
            host.Services.GetRequiredService(type).ShouldNotBeNull(type.Name);
        }

        registered.ShouldContain(typeof(MainWindow), "the window is the only service left out: it needs WPF's XAML runtime");
    }

    private static void StartsBefore<TFirst, TSecond>(List<Type> order, string why)
        where TFirst : IHostedService
        where TSecond : IHostedService
    {
        order.ShouldContain(typeof(TFirst));
        order.ShouldContain(typeof(TSecond));
        order.IndexOf(typeof(TFirst)).ShouldBeLessThan(order.IndexOf(typeof(TSecond)), why);
    }

    /// <summary>The app's builder, validated on build, with the one service that needs a running WPF Application replaced.</summary>
    private static HostApplicationBuilder Builder()
    {
        var builder = App.CreateHostBuilder(TempPaths());
        // The WPF dispatcher exists only inside a running Application; the flows are the same with a synchronous one.
        builder.Services.Replace(ServiceDescriptor.Singleton<IUiDispatcher>(new ImmediateDispatcher()));
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        return builder;
    }

    private static AppPaths TempPaths() => new(Path.Combine(Path.GetTempPath(), "csx-host-" + Guid.NewGuid().ToString("N")));
}
