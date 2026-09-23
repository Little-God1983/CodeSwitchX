using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Ingest.Tests.Api;

public class EventApiServiceTests : IAsyncLifetime
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-api-" + Guid.NewGuid().ToString("N")));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<HookEvent> _received = [];
    private readonly string _pipeName = "csx-test-" + Guid.NewGuid().ToString("N");
    private EventApiService _api = null!;
    private string _token = null!;

    public async ValueTask InitializeAsync()
    {
        _paths.EnsureCreated();
        _bus.Subscribe<HookEventReceived>(m => _received.Add(m.Event));
        var tokens = new AccessTokenStore(_paths);
        _token = tokens.GetOrCreate();
        _api = new EventApiService(_paths, _bus, tokens, TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = _pipeName, LoopbackPort = 0 });
        await _api.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        Directory.Delete(_paths.Root, recursive: true);
    }

    private const string Body = """{"event":"Stop","payload":{"session_id":"s1","hook_event_name":"Stop","cwd":"C:\\x"}}""";

    private HttpClient Loopback() => new() { BaseAddress = new Uri($"http://127.0.0.1:{_api.Endpoint!.Port}/") };

    private HttpClient Pipe()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ct);
                return pipe;
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://pipe/") };
    }

    private static HttpRequestMessage Post(string body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "events") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public void Endpoint_file_describes_the_running_server()
    {
        var descriptor = EndpointDescriptor.TryRead(_paths.EndpointFile).ShouldNotBeNull();

        descriptor.Port.ShouldBe(_api.Endpoint!.Port);
        descriptor.Port.ShouldBeGreaterThan(0);
        descriptor.PipeName.ShouldBe(_pipeName);
        descriptor.Pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public async Task Loopback_post_with_token_is_accepted_and_published()
    {
        using var client = Loopback();

        var response = await client.SendAsync(Post(Body, _token), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _received.ShouldHaveSingleItem().SessionId.ShouldBe("s1");
    }

    [Fact]
    public async Task Named_pipe_post_with_token_is_accepted_and_published()
    {
        using var client = Pipe();

        var response = await client.SendAsync(Post(Body, _token), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _received.ShouldHaveSingleItem().Signal.ShouldBe(SessionSignal.Stop);
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_rejected()
    {
        using var client = Loopback();

        (await client.SendAsync(Post(Body, null), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Post(Body, new string('0', 64)), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Custom_header_is_accepted_too()
    {
        using var client = Loopback();
        var request = new HttpRequestMessage(HttpMethod.Post, "events") { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CodeSwitchX-Token", _token);

        (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Bad_payload_is_a_400_and_health_needs_no_token()
    {
        using var client = Loopback();

        (await client.SendAsync(Post("{\"nope\":true}", _token), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("health", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stopping_removes_the_endpoint_file()
    {
        await _api.StopAsync(CancellationToken.None);

        File.Exists(_paths.EndpointFile).ShouldBeFalse();
    }
}
