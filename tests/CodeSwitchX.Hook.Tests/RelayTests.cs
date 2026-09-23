using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSwitchX.Hook;

namespace CodeSwitchX.Hook.Tests;

public class RelayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csx-relay-" + Guid.NewGuid().ToString("N"));

    public RelayTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Stream Stdin(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Envelope_embeds_json_payload_as_an_object()
    {
        var now = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var chain = new List<ProcessInfo> { new(100, "cmd.exe"), new(200, "claude.exe") };

        var json = Relay.BuildEnvelope("PreToolUse", """{"session_id":"s1","tool_name":"Bash"}""", now, 4242, chain);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("event").GetString().ShouldBe("PreToolUse");
        root.GetProperty("receivedAtUtc").GetDateTimeOffset().ShouldBe(now);
        root.GetProperty("relayPid").GetInt32().ShouldBe(4242);
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(2);
        root.GetProperty("parentChain")[1].GetProperty("name").GetString().ShouldBe("claude.exe");
        root.GetProperty("payload").GetProperty("session_id").GetString().ShouldBe("s1");
    }

    [Fact]
    public void Envelope_keeps_non_json_stdin_as_a_string()
    {
        var json = Relay.BuildEnvelope("Stop", "not json", DateTimeOffset.UtcNow, 1, []);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("payload").GetString().ShouldBe("not json");
    }

    [Fact]
    public void ReadEndpoint_parses_the_descriptor_and_rejects_garbage()
    {
        var file = Path.Combine(_dir, "endpoint.json");
        File.WriteAllText(file, """{"pipeName":"csx-p","port":51234,"pid":7,"startedAtUtc":"2026-09-23T10:00:00Z"}""");

        var endpoint = Relay.ReadEndpoint(file).ShouldNotBeNull();
        endpoint.PipeName.ShouldBe("csx-p");
        endpoint.Port.ShouldBe(51234);

        File.WriteAllText(file, "{ nope");
        Relay.ReadEndpoint(file).ShouldBeNull();
        Relay.ReadEndpoint(Path.Combine(_dir, "missing.json")).ShouldBeNull();
    }

    [Fact]
    public async Task Missing_endpoint_file_exits_zero_quickly()
    {
        var sw = Stopwatch.StartNew();

        var code = await Relay.RunAsync(["Stop"], Stdin("{}"), _dir);

        code.ShouldBe(0);
        sw.ElapsedMilliseconds.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task Dead_port_and_missing_pipe_exit_zero_within_the_budget()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var deadPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        File.WriteAllText(Path.Combine(_dir, "endpoint.json"), $$"""{"pipeName":"csx-nonexistent-{{Guid.NewGuid():N}}","port":{{deadPort}},"pid":1,"startedAtUtc":"2026-09-23T10:00:00Z"}""");
        File.WriteAllText(Path.Combine(_dir, "token"), new string('a', 64));
        var sw = Stopwatch.StartNew();

        var code = await Relay.RunAsync(["Stop"], Stdin("""{"session_id":"s1"}"""), _dir);

        code.ShouldBe(0);
        sw.ElapsedMilliseconds.ShouldBeLessThan(3000);
    }

    [Fact]
    public async Task Missing_token_exits_zero()
    {
        File.WriteAllText(Path.Combine(_dir, "endpoint.json"), """{"pipeName":"","port":1,"pid":1,"startedAtUtc":"2026-09-23T10:00:00Z"}""");

        (await Relay.RunAsync(["Stop"], Stdin("{}"), _dir)).ShouldBe(0);
    }
}
