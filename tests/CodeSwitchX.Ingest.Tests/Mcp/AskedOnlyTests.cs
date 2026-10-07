using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Api;
using CodeSwitchX.Ingest.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>
/// A message from another Claude session starts a turn of a Raven chat's brain, with all its tools (#193). What it asks is
/// no word of the user's, so the server refuses every tool that acts to a Raven chat that is not in its user's question,
/// in one filter for every tool not marked read-only (#200); the looking tools stay open, and a caller that is no Raven
/// chat is let be.
/// </summary>
public sealed class AskedOnlyTests : IAsyncLifetime
{
    private static readonly string InCodeSwitchX = FakeYard.CodeSwitchXId.ToString("D");
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-asked-" + Guid.NewGuid().ToString("N")));
    private readonly FakeYard _yard = new();
    private readonly FakeActions _actions = new();
    private readonly NoSettings _settings = new();
    private readonly AskedChats _asked = new();
    private EventApiService _api = null!;
    private string _token = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _paths.EnsureCreated();
        var tokens = new AccessTokenStore(_paths);
        _token = tokens.GetOrCreate();
        _api = new EventApiService(_paths, new EventBus(NullLogger<EventBus>.Instance), tokens, TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = "csx-test-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 }, _yard, _actions,
            settings: _settings, asked: _asked);
        await _api.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        Directory.Delete(_paths.Root, recursive: true);
    }

    /// <param name="chat">What the caller sends as its chat header; none for a caller that is no Raven chat.</param>
    private Task<McpClient> ConnectAsync(string? chat)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_token}" };
        if (chat is not null)
        {
            headers[YardMcp.ChatHeader] = chat;
        }

        return McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_api.Endpoint!.Port}{YardMcp.Route}"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
        }), cancellationToken: Ct);
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;

    [Fact]
    public async Task Stop_it_is_refused_in_a_turn_of_the_brain_s_own_and_done_in_the_user_s_question()
    {
        await using var client = await ConnectAsync(InCodeSwitchX);

        var refused = await client.CallToolAsync("stop_chat", cancellationToken: Ct);
        refused.IsError.ShouldBe(true);
        Text(refused).ShouldContain(ToolActs.NotAsked);
        _actions.Stopped.ShouldBeNull("a chat's message stops nothing");

        _asked.Begin(InCodeSwitchX);
        var done = await client.CallToolAsync("stop_chat", cancellationToken: Ct);
        done.IsError.ShouldNotBe(true);
        _actions.Stopped.ShouldNotBeNull().Title.ShouldBe("Speech gate");
    }

    [Fact]
    public async Task Every_tool_not_marked_read_only_is_refused_outside_the_user_s_question()
    {
        // Every tool the server lists, by its annotation: one added later, of any type, is refused too.
        await using var client = await ConnectAsync(InCodeSwitchX);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var acting = tools.Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint != true).ToList();
        acting.Select(t => t.Name).ShouldContain("answer_permission");
        acting.Select(t => t.Name).ShouldContain("set_setting");

        foreach (var tool in acting)
        {
            var result = await client.CallToolAsync(tool.Name, cancellationToken: Ct);
            result.IsError.ShouldBe(true, tool.Name);
            Text(result).ShouldContain(ToolActs.NotAsked, Case.Sensitive, tool.Name);
        }

        _actions.Started.ShouldBeNull();
        _actions.Stopped.ShouldBeNull();
        _settings.Touched.ShouldBeFalse();
    }

    [Fact]
    public async Task Looking_stays_open()
    {
        await using var client = await ConnectAsync(InCodeSwitchX);

        var result = await client.CallToolAsync("list_chats", cancellationToken: Ct);

        result.IsError.ShouldNotBe(true);
        Text(result).ShouldContain("Speech gate");
    }

    [Fact]
    public async Task Chat_0_acts_in_its_user_s_question_too()
    {
        await using var client = await ConnectAsync(YardMcp.OverviewChat);
        (await client.CallToolAsync("set_defaults", new Dictionary<string, object?> { ["model"] = "Opus" }, cancellationToken: Ct)).IsError.ShouldBe(true);

        _asked.Begin(YardMcp.OverviewChat);
        (await client.CallToolAsync("set_defaults", new Dictionary<string, object?> { ["model"] = "Opus" }, cancellationToken: Ct)).IsError.ShouldNotBe(true);
    }

    [Fact]
    public async Task A_chat_header_that_names_no_Raven_chat_is_refused()
    {
        await using var client = await ConnectAsync("not a window");

        var result = await client.CallToolAsync("stop_chat", new Dictionary<string, object?> { ["chat"] = "aaaaaaaa" }, cancellationToken: Ct);

        result.IsError.ShouldBe(true);
        Text(result).ShouldContain(ToolActs.NotAsked);
        _actions.Stopped.ShouldBeNull();
    }

    [Fact]
    public async Task A_chat_whose_Claude_Code_sends_no_question_ids_is_told_why_it_cannot_act()
    {
        // #199: the brain must not tell the user a chat's message asked for it.
        await using var client = await ConnectAsync(InCodeSwitchX);
        _asked.Unverified(InCodeSwitchX, true);

        Text(await client.CallToolAsync("stop_chat", cancellationToken: Ct)).ShouldContain(ToolActs.NoIds);
    }

    [Fact]
    public async Task A_caller_that_is_no_Raven_chat_is_let_be()
    {
        // It sends no chat header: no brain of Raven's says when it is in a question.
        await using var client = await ConnectAsync(null);

        (await client.CallToolAsync("stop_chat", new Dictionary<string, object?> { ["chat"] = "aaaaaaaa" }, cancellationToken: Ct)).IsError.ShouldNotBe(true);
        _actions.Stopped.ShouldNotBeNull();
    }

    /// <summary>Settings that only say whether anything reached them.</summary>
    private sealed class NoSettings : IAppSettings
    {
        public bool Touched { get; private set; }

        public Task<IReadOnlyList<AppSetting>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AppSetting>>([]);

        public Task<AppSettingValue> GetAsync(string name, CancellationToken ct) => Task.FromResult(new AppSettingValue(name, "Voice", "on", null));

        public Task<AppSettingValue> SetAsync(string name, string value, CancellationToken ct)
        {
            Touched = true;
            return Task.FromResult(new AppSettingValue(name, "Voice", value, null));
        }

        public Task<string> OpenAsync(string? page, CancellationToken ct)
        {
            Touched = true;
            return Task.FromResult(page ?? "Voice");
        }
    }
}
