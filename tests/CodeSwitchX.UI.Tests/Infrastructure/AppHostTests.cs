using CodeSwitchX.Core;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Data;
using CodeSwitchX.Hosting;
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
            using var host = App.CreateHostBuilder().Build();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The flows depend on the order the hosted services start in, which is only the order of four registration calls:
    /// the writer must subscribe before Kestrel opens the pipe, or the first hook events are never saved, and
    /// telemetry must subscribe before the indexer's first scan, or the usage that scan finds before the writer
    /// flushed it is in neither the database telemetry loaded nor its memory until the next start.
    /// </summary>
    [Fact]
    public void Hosted_services_start_in_the_order_the_flows_depend_on()
    {
        using var provider = BuildProvider();

        var order = provider.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        order.ShouldBe([typeof(ProcessLivenessMonitor), typeof(PersistenceWriter), typeof(TelemetryService), typeof(EventApiService), typeof(TranscriptIndexer), typeof(HiddenWindowSweep)]);
    }

    /// <summary>A constructor parameter without a registration otherwise shows up only as "CodeSwitchX failed to start" at the next manual run.</summary>
    [Fact]
    public void Every_registered_service_can_be_built()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, TempPaths());
        // Every CodeSwitchX service the root resolves; the pooled DbContext is scoped and is only ever made by its factory.
        var registered = services.Where(d => d.Lifetime != ServiceLifetime.Scoped).Select(d => d.ServiceType).Distinct()
            .Where(t => t.Namespace?.StartsWith("CodeSwitchX", StringComparison.Ordinal) == true).ToList();
        using var provider = BuildProvider(services);

        foreach (var type in registered.Where(t => t != typeof(MainWindow)))
        {
            provider.GetRequiredService(type).ShouldNotBeNull(type.Name);
        }

        registered.ShouldContain(typeof(MainWindow), "the window is the only service left out: it needs WPF's XAML runtime");
    }

    private static ServiceProvider BuildProvider(ServiceCollection? services = null)
    {
        services ??= new ServiceCollection();
        if (services.Count == 0)
        {
            App.ConfigureServices(services, TempPaths());
        }

        services.AddLogging();
        // The WPF dispatcher exists only inside a running Application; the flows are the same with a synchronous one.
        services.Replace(ServiceDescriptor.Singleton<IUiDispatcher>(new ImmediateDispatcher()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static AppPaths TempPaths() => new(Path.Combine(Path.GetTempPath(), "csx-host-" + Guid.NewGuid().ToString("N")));
}
