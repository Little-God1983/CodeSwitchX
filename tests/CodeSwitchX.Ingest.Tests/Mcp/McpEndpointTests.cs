using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Api;
using CodeSwitchX.Ingest.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeSwitchX.Ingest.Tests.Mcp;

public sealed class McpEndpointTests : IAsyncLifetime
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-mcp-" + Guid.NewGuid().ToString("N")));
    private readonly FakeYard _yard = new();
    private EventApiService _api = null!;
    private string _token = null!;

    public async ValueTask InitializeAsync()
    {
        _paths.EnsureCreated();
        var tokens = new AccessTokenStore(_paths);
        _token = tokens.GetOrCreate();
        _api = new EventApiService(_paths, new EventBus(NullLogger<EventBus>.Instance), tokens, TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = "csx-test-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 }, _yard);
        await _api.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        Directory.Delete(_paths.Root, recursive: true);
    }

    private Uri Url => new($"http://127.0.0.1:{_api.Endpoint!.Port}{YardMcp.Route}");

    /// <param name="window">The workspace a window chat's brain names in its header; none for the Yard's.</param>
    private Task<McpClient> ConnectAsync(string token, Guid? window = null) => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = Url,
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = window is { } id
            ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}", [YardMcp.ChatHeader] = id.ToString("D") }
            : new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
    }), cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_window_chat_s_brain_sees_its_window_s_chats_by_its_header()
    {
        await using var window = await ConnectAsync(_token, FakeYard.DiffusionId);
        await using var yard = await ConnectAsync(_token);

        string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
        var own = Text(await window.CallToolAsync("list_chats", cancellationToken: TestContext.Current.CancellationToken));
        var all = Text(await yard.CallToolAsync("list_chats", cancellationToken: TestContext.Current.CancellationToken));

        own.ShouldContain("Installer icons");
        own.ShouldNotContain("Speech gate");
        all.ShouldContain("Installer icons");
        all.ShouldContain("Speech gate");
    }

    [Fact]
    public async Task The_brain_sees_the_four_read_only_tools()
    {
        await using var client = await ConnectAsync(_token);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(t => t.Name).Order().ShouldBe(["find_workspace", "get_chat", "list_chats", "list_workspaces"]);
        tools.ShouldAllBe(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true);
    }

    [Fact]
    public async Task What_is_waiting_on_me_comes_back_as_text_the_model_reads()
    {
        await using var client = await ConnectAsync(_token);

        var result = await client.CallToolAsync("list_chats", new Dictionary<string, object?> { ["filter"] = "needs_me" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldNotBe(true);
        var text = result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
        text.ShouldContain("Raven brain");
        text.ShouldContain("needs you");
        text.ShouldNotContain("Speech gate");
    }

    [Fact]
    public async Task A_refused_call_reaches_the_model_as_an_error_it_can_read()
    {
        await using var client = await ConnectAsync(_token);

        var result = await client.CallToolAsync("list_chats", new Dictionary<string, object?> { ["filter"] = "busy" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text.ShouldContain("needs_me");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Without_the_token_the_tools_answer_nothing(string? token)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void The_config_file_tells_Claude_Code_where_the_tools_are_and_the_token()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(_paths.McpConfigFile));

        var server = config.RootElement.GetProperty("mcpServers").GetProperty(YardMcp.ServerName);
        server.GetProperty("type").GetString().ShouldBe("http");
        server.GetProperty("url").GetString().ShouldBe(Url.ToString());
        server.GetProperty("headers").GetProperty("Authorization").GetString().ShouldBe($"Bearer {_token}");
    }

    [Fact]
    public void Only_the_current_user_may_read_the_config_file()
    {
        var rules = new FileInfo(_paths.McpConfigFile).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();

        rules.ShouldHaveSingleItem().IdentityReference.ShouldBe(WindowsIdentity.GetCurrent().User);
    }

    [Fact]
    public async Task A_config_file_that_cannot_be_written_leaves_the_hook_pipe_running_and_the_stale_one_gone()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "csx-mcp-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        File.WriteAllText(paths.McpConfigFile, """{"mcpServers":{"codeswitchx":{"url":"http://127.0.0.1:1/mcp"}}}"""); // an earlier run's
        Directory.CreateDirectory(paths.McpConfigFile + ".tmp"); // a folder where the new file is written first: the write fails
        var api = new EventApiService(paths, new EventBus(NullLogger<EventBus>.Instance), new AccessTokenStore(paths), TimeProvider.System,
            NullLoggerFactory.Instance, new EventApiOptions { PipeName = "csx-test-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 }, _yard);
        try
        {
            await api.StartAsync(CancellationToken.None);

            api.Endpoint.ShouldNotBeNull().Port.ShouldBeGreaterThan(0);
            File.Exists(paths.McpConfigFile).ShouldBeFalse("the brain must not call the earlier run's address");
        }
        finally
        {
            await api.StopAsync(CancellationToken.None);
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task The_config_file_goes_when_the_server_stops()
    {
        await _api.StopAsync(CancellationToken.None);

        File.Exists(_paths.McpConfigFile).ShouldBeFalse();
        await _api.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Given_what_can_be_done_the_brain_also_sees_the_action_tools_and_their_refusals()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "csx-mcp-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var tokens = new AccessTokenStore(paths);
        var actions = new FakeActions { Refusal = "VS Code could not be opened for CodeSwitchX: VS Code executable not found." };
        var api = new EventApiService(paths, new EventBus(NullLogger<EventBus>.Instance), tokens, TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = "csx-test-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 }, _yard, actions);
        try
        {
            await api.StartAsync(CancellationToken.None);
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{api.Endpoint!.Port}{YardMcp.Route}"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {tokens.GetOrCreate()}" },
            }), cancellationToken: TestContext.Current.CancellationToken);

            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            var start = await client.CallToolAsync("start_chat", new Dictionary<string, object?> { ["workspace"] = "CodeSwitchX" },
                cancellationToken: TestContext.Current.CancellationToken);

            tools.Select(t => t.Name).Order().ShouldBe(["answer_permission", "answer_question", "back_to_yard", "close_chat", "find_workspace", "get_chat", "list_chats",
                "list_workspaces", "open_workspace", "set_defaults", "start_chat", "stop_chat", "switch_chat"]);
            tools.Where(t => t.ProtocolTool.Annotations!.DestructiveHint == true).Select(t => t.Name)
                .ShouldBe(["close_chat"], "closing a chat cuts off what it is doing; nothing else Raven does on the Yard destroys anything");
            start.IsError.ShouldBe(true);
            start.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text.ShouldContain("VS Code executable not found.");
        }
        finally
        {
            await api.StopAsync(CancellationToken.None);
            Directory.Delete(paths.Root, recursive: true);
        }
    }
}
