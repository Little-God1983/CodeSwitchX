using System.Text;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hook;
using CodeSwitchX.Ingest.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Hook.Tests;

public class RelayEndToEndTests : IAsyncLifetime
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-e2e-" + Guid.NewGuid().ToString("N")));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<HookEvent> _received = [];
    private EventApiService _api = null!;

    public async ValueTask InitializeAsync()
    {
        _paths.EnsureCreated();
        _bus.Subscribe<HookEventReceived>(m => _received.Add(m.Event));
        _api = new EventApiService(_paths, _bus, new AccessTokenStore(_paths), TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = "csx-e2e-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 });
        await _api.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        Directory.Delete(_paths.Root, recursive: true);
    }

    private static Stream Stdin(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Relay_delivers_over_the_named_pipe()
    {
        var code = await Relay.RunAsync(["PreToolUse"], Stdin("""{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Bash","cwd":"C:\\Repo"}"""), _paths.Root);

        code.ShouldBe(0);
        var e = _received.ShouldHaveSingleItem();
        e.SessionId.ShouldBe("s1");
        e.Signal.ShouldBe(SessionSignal.ToolUse);
        e.ToolName.ShouldBe("Bash");
        e.RelayPid.ShouldBe(Environment.ProcessId);
        e.ParentChain.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Relay_falls_back_to_loopback_when_the_pipe_is_gone()
    {
        var descriptor = EndpointDescriptor.TryRead(_paths.EndpointFile)!;
        (descriptor with { PipeName = "csx-gone-" + Guid.NewGuid().ToString("N") }).Write(_paths.EndpointFile);

        var code = await Relay.RunAsync(["Stop"], Stdin("""{"session_id":"s2","hook_event_name":"Stop"}"""), _paths.Root);

        code.ShouldBe(0);
        _received.ShouldHaveSingleItem().SessionId.ShouldBe("s2");
    }
}
