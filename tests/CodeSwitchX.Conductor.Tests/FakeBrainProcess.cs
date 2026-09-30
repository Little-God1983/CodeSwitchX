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

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
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

    /// <summary>The process dies: its output closes and it exits.</summary>
    public void Die(int code)
    {
        _lines.Writer.TryComplete();
        _exited.TrySetResult(code);
    }

    public void Dispose()
    {
        Disposed = true;
        Die(1);
    }
}

internal sealed class FakeLauncher : IBrainProcessLauncher
{
    public List<(string Executable, IReadOnlyList<string> Arguments, string Folder, FakeBrainProcess Process)> Started { get; } = [];

    /// <summary>What each new process answers with.</summary>
    public Func<string, IEnumerable<string>> Answer { get; set; } = StreamJson.Reply("Hi.");

    public Exception? Failure { get; set; }

    public FakeBrainProcess Last => Started[^1].Process;

    public IBrainProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        var process = new FakeBrainProcess { Answer = Answer };
        Started.Add((executable, arguments, workingDirectory, process));
        return process;
    }
}

/// <summary>Lines shaped like CLI 2.1.285's (captured 2026-09-30), cut down to what the brain reads.</summary>
internal static class StreamJson
{
    public static string Init(string status = "connected") =>
        $$"""{"type":"system","subtype":"init","model":"claude-haiku-4-5-20251001","permissionMode":"dontAsk","mcp_servers":[{"name":"codeswitchx","status":"{{status}}"}],"tools":["mcp__codeswitchx__list_chats"]}""";

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

    public const string ErrorResult =
        """{"type":"result","subtype":"success","is_error":true,"result":"API Error: 529 Overloaded","session_id":"s"}""";

    /// <summary>Answers every turn with init, the text in two pieces and the result.</summary>
    public static Func<string, IEnumerable<string>> Reply(string text) =>
        _ => [Init(), Text(text[..(text.Length / 2)]), Text(text[(text.Length / 2)..]), AssistantText(text), Result(text)];

    private static string Parent(string? parent) => parent is null ? "null" : $"\"{parent}\"";
}
