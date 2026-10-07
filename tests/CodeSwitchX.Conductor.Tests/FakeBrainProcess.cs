using System.Text.Json;
using System.Threading.Channels;

namespace CodeSwitchX.Conductor.Tests;

/// <summary>A claude that answers each line written to it with <see cref="Answer"/>, as the real one would, in stream-json.</summary>
internal sealed class FakeBrainProcess : IBrainProcess
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Written { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>The lines it writes back for a line written to it; nothing by default.</summary>
    public Func<string, IEnumerable<string>> Answer { get; set; } = _ => [];

    public ChannelReader<string> Lines => _lines.Reader;

    public Task<int> Exited => _exited.Task;

    public string ErrorTail => "error: something broke";

    /// <summary>
    /// Its input is full and it reads no more: a write hangs, deaf to its token as a pipe write blocked in WriteFile is,
    /// until the process is killed, which breaks the pipe.
    /// </summary>
    public bool WritesHang { get; set; }

    private readonly TaskCompletionSource _broken = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (WritesHang)
        {
            Written.Add(line);
            return _broken.Task;
        }

        ct.ThrowIfCancellationRequested(); // as StreamWriter does, before it writes
        if (_exited.Task.IsCompleted)
        {
            throw new IOException("The pipe has been ended.");
        }

        Written.Add(line);
        foreach (var answer in Answer(line))
        {
            _lines.Writer.TryWrite(answer);
        }

        return Task.CompletedTask;
    }

    public void Emit(string line) => _lines.Writer.TryWrite(line);

    public bool InputClosed { get; private set; }

    /// <summary>Whether closing the input ends it, as it ends the real one between turns; true by default.</summary>
    public bool ExitsOnClosedInput { get; set; } = true;

    public void CloseInput()
    {
        InputClosed = true;
        if (ExitsOnClosedInput)
        {
            Die(0);
        }
    }

    /// <summary>The process dies: its output closes and it exits.</summary>
    public void Die(int code)
    {
        _lines.Writer.TryComplete();
        _exited.TrySetResult(code);
    }

    public void Dispose()
    {
        Disposed = true;
        _broken.TrySetException(new IOException("The pipe has been ended."));
        Die(1);
    }
}

internal sealed class FakeLauncher : IBrainProcessLauncher
{
    public List<(string Executable, IReadOnlyList<string> Arguments, string Folder, FakeBrainProcess Process)> Started { get; } = [];

    /// <summary>The environment each process was started with, in the order of <see cref="Started"/>.</summary>
    public List<IReadOnlyDictionary<string, string?>?> Environments { get; } = [];

    /// <summary>What each new process answers with.</summary>
    public Func<string, IEnumerable<string>> Answer { get; set; } = StreamJson.Reply("Hi.");

    public Exception? Failure { get; set; }

    public FakeBrainProcess Last => Started[^1].Process;

    public IBrainProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        var process = new FakeBrainProcess { Answer = Answer };
        Started.Add((executable, arguments, workingDirectory, process));
        Environments.Add(environment);
        return process;
    }
}

/// <summary>Lines shaped like CLI 2.1.285's (captured 2026-09-30), cut down to what the brain reads.</summary>
internal static class StreamJson
{
    /// <param name="send">Whether this Claude Code has the SendMessage tool.</param>
    public static string Init(string status = "connected", string mode = "dontAsk", bool send = true) =>
        $$"""{"type":"system","subtype":"init","model":"claude-haiku-4-5-20251001","permissionMode":"{{mode}}","mcp_servers":[{"name":"codeswitchx","status":"{{status}}"}],"tools":[{{(send ? "\"SendMessage\"," : "")}}"mcp__codeswitchx__list_chats"]}""";

    public static string Text(string text, string? parent = null) =>
        $$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"{{{text}}}"}},"parent_tool_use_id":{{{Parent(parent)}}}}""";

    public const string Thinking =
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hm"}},"parent_tool_use_id":null}""";

    public static string ToolUse(string id, string name, string input = "{}") =>
        $$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{input}}}]},"parent_tool_use_id":null}""";

    public static string ToolResult(string id, bool error = false) =>
        $$"""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{id}}","is_error":{{(error ? "true" : "false")}},"content":"[]"}]},"parent_tool_use_id":null}""";

    public static string AssistantText(string text) =>
        $$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"{{text}}"}]},"parent_tool_use_id":null}""";

    public static string Result(string text = "Hi.") =>
        $$"""{"type":"result","subtype":"success","is_error":false,"result":"{{text}}","session_id":"s"}""";

    /// <summary>What Claude Code writes, at once and before its init, for a <c>--resume</c> of a session it does not have (CLI 2.1.285).</summary>
    public const string NoConversation =
        """{"type":"result","subtype":"error_during_execution","duration_ms":0,"is_error":true,"num_turns":0,"session_id":"s"}""";

    /// <summary>The message Claude Code writes itself for a model that does not exist (CLI 2.1.286), before the failed result.</summary>
    public const string ModelNotFound =
        """{"type":"assistant","message":{"model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"There's an issue with the selected model (claude-opus-5-6)."}]},"parent_tool_use_id":null,"error":"model_not_found"}""";

    public const string ModelNotFoundResult =
        """{"type":"result","subtype":"success","is_error":true,"result":"There's an issue with the selected model (claude-opus-5-6).","session_id":"s"}""";

    /// <summary>A line written to it, echoed as it is taken into a turn (<c>--replay-user-messages</c>).</summary>
    public static string Taken(string written)
    {
        var line = System.Text.Json.Nodes.JsonNode.Parse(written)!.AsObject();
        line["parent_tool_use_id"] = null;
        line["session_id"] = "s";
        line["isReplay"] = true;
        return line.ToJsonString();
    }

    /// <summary>
    /// A message from another Claude session (<c>SendMessage</c>), echoed as it is taken into a turn (CLI 2.1.292, captured
    /// 2026-10-07, the envelope cut down).
    /// </summary>
    public static string PeerTaken(string message) => new System.Text.Json.Nodes.JsonObject
    {
        ["type"] = "user",
        ["message"] = new System.Text.Json.Nodes.JsonObject
        {
            ["role"] = "user",
            ["content"] = $"Another Claude session sent a message:\n<cross-session-message from=\"uds:x\" from-name=\"bug-report-1\" from-mode=\"prompting\">\n{message}\n</cross-session-message>",
        },
        ["parent_tool_use_id"] = null,
        ["session_id"] = "s",
        ["uuid"] = Guid.NewGuid().ToString("D"),
        ["isReplay"] = true,
        ["isSynthetic"] = true,
        ["origin"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "peer", ["from"] = "uds:x", ["name"] = "bug-report-1", ["body"] = message },
    }.ToJsonString();

    public const string ErrorResult =
        """{"type":"result","subtype":"success","is_error":true,"result":"API Error: 529 Overloaded","session_id":"s"}""";

    /// <summary>The line the brain writes to interrupt a turn (control request, subtype interrupt).</summary>
    public static bool IsInterrupt(string written)
    {
        using var line = JsonDocument.Parse(written);
        return line.RootElement.GetProperty("type").GetString() == "control_request"
            && line.RootElement.GetProperty("request").GetProperty("subtype").GetString() == "interrupt";
    }

    /// <summary>What CLI 2.1.285 answers an interrupt with (captured 2026-10-01).</summary>
    public static string InterruptAck(string written)
    {
        using var line = JsonDocument.Parse(written);
        var id = line.RootElement.GetProperty("request_id").GetString();
        return new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new System.Text.Json.Nodes.JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = id,
                ["response"] = new System.Text.Json.Nodes.JsonObject { ["still_queued"] = new System.Text.Json.Nodes.JsonArray() },
            },
        }.ToJsonString();
    }

    /// <summary>The result that ends an interrupted turn (CLI 2.1.285).</summary>
    public const string InterruptedResult =
        """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s"}""";

    /// <summary>Answers every turn with init, the text in two pieces and the result.</summary>
    public static Func<string, IEnumerable<string>> Reply(string text) =>
        _ => [Init(), Text(text[..(text.Length / 2)]), Text(text[(text.Length / 2)..]), AssistantText(text), Result(text)];

    private static string Parent(string? parent) => parent is null ? "null" : $"\"{parent}\"";
}
