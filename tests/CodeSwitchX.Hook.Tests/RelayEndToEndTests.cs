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
    private readonly StringWriter _stdout = new();
    private TurnStops _stops = null!;
    private EventApiService _api = null!;

    public async ValueTask InitializeAsync()
    {
        _paths.EnsureCreated();
        _bus.Subscribe<HookEventReceived>(m => _received.Add(m.Event));
        _stops = new TurnStops(_bus, TimeProvider.System);
        _api = new EventApiService(_paths, _bus, new AccessTokenStore(_paths), TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = "csx-e2e-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 }, stops: _stops);
        await _api.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        _stops.Dispose();
        Directory.Delete(_paths.Root, recursive: true);
    }

    private static Stream Stdin(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Relay_delivers_over_the_named_pipe()
    {
        var code = await Relay.RunAsync(["PreToolUse"], Stdin("""{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Bash","cwd":"C:\\Repo"}"""), _stdout, _paths.Root);

        code.ShouldBe(0);
        var e = _received.ShouldHaveSingleItem();
        e.SessionId.ShouldBe("s1");
        e.Signal.ShouldBe(SessionSignal.ToolUse);
        e.ToolName.ShouldBe("Bash");
        e.RelayPid.ShouldBe(Environment.ProcessId);
        e.ParentChain.ShouldNotBeEmpty();
        _stdout.ToString().ShouldBeEmpty("nothing is said to Claude Code unless its turn is to stop");
    }

    [Fact]
    public async Task A_stop_asked_for_the_chat_ends_its_turn_and_denies_the_step_it_was_about_to_take()
    {
        var stopped = _stops.Request("s1");

        var code = await Relay.RunAsync(["PreToolUse"], Stdin("""{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Bash"}"""), _stdout, _paths.Root);

        code.ShouldBe(0);
        (await stopped).ShouldBe(TurnStopOutcome.Stopped);
        using var answer = System.Text.Json.JsonDocument.Parse(_stdout.ToString());
        answer.RootElement.GetProperty("continue").GetBoolean().ShouldBeFalse();
        answer.RootElement.GetProperty("stopReason").GetString().ShouldBe(TurnStops.Reason);
        var specific = answer.RootElement.GetProperty("hookSpecificOutput");
        specific.GetProperty("hookEventName").GetString().ShouldBe("PreToolUse");
        specific.GetProperty("permissionDecision").GetString().ShouldBe("deny");
        _received.Select(e => (e.EventName, e.Signal)).ShouldBe([("PreToolUse", SessionSignal.ToolUse), ("Stop", SessionSignal.Stop)],
            "the step still reaches the Yard, and so does the end of the turn, which Claude Code does not send itself");
        _received[1].SessionId.ShouldBe("s1");
    }

    [Fact]
    public async Task A_stop_after_a_step_ends_the_turn_without_a_deny_and_is_taken_once()
    {
        _ = _stops.Request("s1");

        await Relay.RunAsync(["PostToolUse"], Stdin("""{"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Bash"}"""), _stdout, _paths.Root);
        var first = _stdout.ToString();
        await Relay.RunAsync(["PostToolUse"], Stdin("""{"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Read"}"""), _stdout, _paths.Root);

        first.ShouldBe($$"""{"continue":false,"stopReason":"{{TurnStops.Reason}}"}""");
        _stdout.ToString().ShouldBe(first, "a stop ends one turn, not the next");
    }

    [Fact]
    public async Task An_older_relay_that_does_not_say_it_hands_a_stop_on_is_not_given_one()
    {
        var stopped = _stops.Request("s1");
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_api.Endpoint!.Port}/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "events")
        {
            Content = new StringContent("""{"event":"PreToolUse","payload":{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Bash"}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new AccessTokenStore(_paths).GetOrCreate());

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        (await stopped).ShouldBe(TurnStopOutcome.OldRelay, "it would never land, and Raven says so");
        _stops.CanStop("s1").ShouldBe(false);
    }

    [Fact]
    public async Task Another_chat_s_steps_and_events_that_are_no_step_do_not_take_the_stop()
    {
        var stopped = _stops.Request("s1");

        await Relay.RunAsync(["PreToolUse"], Stdin("""{"session_id":"s2","hook_event_name":"PreToolUse","tool_name":"Bash"}"""), _stdout, _paths.Root);
        await Relay.RunAsync(["Notification"], Stdin("""{"session_id":"s1","hook_event_name":"Notification","message":"Claude is waiting"}"""), _stdout, _paths.Root);

        _stdout.ToString().ShouldBeEmpty();
        stopped.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Relay_falls_back_to_loopback_when_the_pipe_is_gone()
    {
        var descriptor = EndpointDescriptor.TryRead(_paths.EndpointFile)!;
        (descriptor with { PipeName = "csx-gone-" + Guid.NewGuid().ToString("N") }).Write(_paths.EndpointFile);

        var code = await Relay.RunAsync(["Stop"], Stdin("""{"session_id":"s2","hook_event_name":"Stop"}"""), _stdout, _paths.Root);

        code.ShouldBe(0);
        _received.ShouldHaveSingleItem().SessionId.ShouldBe("s2");
    }

    [Fact]
    public async Task Relay_treats_an_endpoint_whose_owner_process_is_gone_as_absent()
    {
        var descriptor = EndpointDescriptor.TryRead(_paths.EndpointFile)!;
        (descriptor with { Pid = int.MaxValue - 7 }).Write(_paths.EndpointFile);

        var code = await Relay.RunAsync(["Stop"], Stdin("""{"session_id":"stale","hook_event_name":"Stop"}"""), _stdout, _paths.Root);

        code.ShouldBe(0);
        _received.ShouldBeEmpty("a stale endpoint.json left by a crashed or killed instance must not be trusted");
    }

    [Fact]
    public async Task Relay_treats_an_endpoint_written_before_its_owner_process_started_as_stale()
    {
        // The PID of a crashed instance, reused by this process: the descriptor carries the crashed owner's start time, not this process's.
        var descriptor = EndpointDescriptor.TryRead(_paths.EndpointFile)!;
        (descriptor with { OwnerStartedAtUtc = descriptor.OwnerStartedAtUtc.AddDays(-1) }).Write(_paths.EndpointFile);

        var code = await Relay.RunAsync(["Stop"], Stdin("""{"session_id":"reused","hook_event_name":"Stop"}"""), _stdout, _paths.Root);

        code.ShouldBe(0);
        _received.ShouldBeEmpty("a reused PID must not make a stale endpoint.json look alive");
    }

    [Fact]
    public async Task Relay_delivers_an_event_whose_raw_payload_exceeds_the_api_body_limit()
    {
        var payload = $$"""{"session_id":"big","hook_event_name":"PostToolUse","tool_name":"Read","tool_response":"{{new string('z', 2 * 1024 * 1024)}}"}""";

        var code = await Relay.RunAsync(["PostToolUse"], Stdin(payload), _stdout, _paths.Root);

        code.ShouldBe(0);
        var e = _received.ShouldHaveSingleItem();
        e.SessionId.ShouldBe("big");
        e.ToolName.ShouldBe("Read");
    }
}
