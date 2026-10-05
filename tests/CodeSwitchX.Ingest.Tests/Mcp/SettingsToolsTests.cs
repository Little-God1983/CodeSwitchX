using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Api;
using CodeSwitchX.Ingest.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>The settings tools (#126): what the app says of a setting reaches the brain, and a refusal comes back as words.</summary>
public sealed class SettingsToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSettings : IAppSettings
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<AppSetting> Settings { get; } = [new("open mic", "Listening", "Listens all the time.", ["on", "off"])];

        public IReadOnlyList<string> Pages { get; } = ["Voice", "Listening"];

        public Task<AppSettingValue> GetAsync(string name, CancellationToken ct)
        {
            Calls.Add($"get {name}");
            return name == "open mic" ? Task.FromResult(new AppSettingValue("open mic", "Listening", "on", ["on", "off"]))
                : throw new YardActionException($"There is no setting '{name}'.");
        }

        public Task<AppSettingValue> SetAsync(string name, string value, CancellationToken ct)
        {
            Calls.Add($"set {name}={value}");
            return name == "hooks" ? throw new YardActionException("Hooks is not changed by voice. Settings is open at Claude Code.")
                : Task.FromResult(new AppSettingValue(name, "Listening", value, ["on", "off"]));
        }

        public Task<string> OpenAsync(string? page, CancellationToken ct)
        {
            Calls.Add($"open {page}");
            return Task.FromResult(page ?? "Voice");
        }
    }

    [Fact]
    public async Task The_tools_pass_the_setting_on_and_a_refusal_comes_back_as_an_error_in_words()
    {
        var settings = new FakeSettings();
        var tools = new SettingsTools(settings);

        (await tools.GetSetting("open mic", Ct)).Value.ShouldBe("on");
        (await tools.SetSetting("open mic", "off", Ct)).Value.ShouldBe("off");
        (await tools.OpenSettings("Listening", Ct)).ShouldBe("Settings is open at Listening.");
        (await Should.ThrowAsync<McpException>(() => tools.SetSetting("hooks", "off", Ct))).Message.ShouldContain("not changed by voice");
        (await Should.ThrowAsync<McpException>(() => tools.GetSetting("colour", Ct))).Message.ShouldContain("no setting 'colour'");
        tools.ListSettings().ShouldHaveSingleItem().Name.ShouldBe("open mic");
        settings.Calls.ShouldBe(["get open mic", "set open mic=off", "open Listening", "set hooks=off", "get colour"]);
    }

    [Fact]
    public async Task Given_the_settings_the_brain_sees_their_tools_over_mcp()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "csx-mcp-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var tokens = new AccessTokenStore(paths);
        var api = new EventApiService(paths, new EventBus(NullLogger<EventBus>.Instance), tokens, TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = "csx-test-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 }, new FakeYard(), new FakeActions(),
            settings: new FakeSettings());
        try
        {
            await api.StartAsync(CancellationToken.None);
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{api.Endpoint!.Port}{YardMcp.Route}"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {tokens.GetOrCreate()}" },
            }), cancellationToken: Ct);

            var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
            var refused = await client.CallToolAsync("set_setting", new Dictionary<string, object?> { ["name"] = "hooks", ["value"] = "off" },
                cancellationToken: Ct);

            tools.ShouldContain("get_setting");
            tools.ShouldContain("set_setting");
            tools.ShouldContain("open_settings");
            tools.ShouldContain("list_settings");
            refused.IsError.ShouldBe(true);
            refused.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text.ShouldContain("not changed by voice");
        }
        finally
        {
            await api.StopAsync(CancellationToken.None);
            Directory.Delete(paths.Root, recursive: true);
        }
    }
}
