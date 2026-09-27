using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Ingest.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Ingest.Tests.Api;

/// <summary>Tests that change the process's current directory; they run on their own, after every parallel test.</summary>
[CollectionDefinition(nameof(CurrentDirectoryCollection), DisableParallelization = true)]
public sealed class CurrentDirectoryCollection;

[Collection(nameof(CurrentDirectoryCollection))]
public class EventApiBindingTests
{
    [Fact]
    public async Task Endpoints_from_an_appsettings_json_in_the_current_directory_are_not_bound()
    {
        // Started from a terminal inside an ASP.NET Core project, the Event API also listened wherever that project's
        // Kestrel:Endpoints pointed, 0.0.0.0 included. 127.0.0.2 is loopback as well, so this raises no firewall prompt.
        var root = Path.Combine(Path.GetTempPath(), "csx-bind-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "data"));
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var extra = new IPEndPoint(IPAddress.Parse("127.0.0.2"), FreePort(IPAddress.Parse("127.0.0.2")));
        var settings = new JsonObject { ["Kestrel"] = new JsonObject { ["Endpoints"] = new JsonObject { ["Extra"] = new JsonObject { ["Url"] = $"http://{extra}" } } } };
        File.WriteAllText(Path.Combine(project, "appsettings.json"), settings.ToJsonString());
        var api = new EventApiService(paths, new EventBus(NullLogger<EventBus>.Instance), new AccessTokenStore(paths), TimeProvider.System,
            NullLoggerFactory.Instance, new EventApiOptions { PipeName = "csx-bind-" + Guid.NewGuid().ToString("N") });
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(project);
        try
        {
            await api.StartAsync(CancellationToken.None);

            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().ShouldNotContain(extra);
            api.Endpoint.ShouldNotBeNull().Port.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            await api.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_malformed_appsettings_json_in_the_current_directory_does_not_stop_the_start()
    {
        // The web builder's defaults parse the current directory's appsettings.json while it is created; a half-edited one
        // in the folder CodeSwitchX was started from failed the start.
        var root = Path.Combine(Path.GetTempPath(), "csx-bind-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "data"));
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, "appsettings.json"), """{ "Logging": {""");
        var api = new EventApiService(paths, new EventBus(NullLogger<EventBus>.Instance), new AccessTokenStore(paths), TimeProvider.System,
            NullLoggerFactory.Instance, new EventApiOptions { PipeName = "csx-bind-" + Guid.NewGuid().ToString("N") });
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(project);
        try
        {
            await api.StartAsync(CancellationToken.None);

            api.Endpoint.ShouldNotBeNull().Port.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            await api.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    private static int FreePort(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
