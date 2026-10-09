using System.Text.Json;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Conductor;

/// <summary>Compacts a chat's conversation as <c>/compact</c> typed in its tab would (#226).</summary>
public interface IChatCompactor
{
    /// <summary>
    /// Compacts the conversation on disk; returns once it is done. Its tab must be closed: an open one holds the whole
    /// conversation and would carry on from that.
    /// </summary>
    /// <param name="folder">The folder the chat runs in: Claude Code finds a conversation by it.</param>
    /// <param name="keep">What the summary is to keep ("the test plan"); null for a plain <c>/compact</c>.</param>
    /// <exception cref="YardActionException">It was not compacted; the message says why.</exception>
    Task CompactAsync(string sessionId, string folder, string? keep, CancellationToken ct);

    /// <summary>
    /// Names the chat as <c>/rename</c> typed in its tab would. VS Code lists, and opens again, only a chat with a name: one
    /// that was only ever messaged by another session (Raven) has none (#226). Its tab may be open: the tab takes the name
    /// up at its next write, and goes on from its own last step (spike, #228).
    /// </summary>
    /// <exception cref="YardActionException">It was not named; the message says why.</exception>
    Task NameAsync(string sessionId, string folder, string name, CancellationToken ct);
}

/// <summary>
/// Claude Code compacts it, headless: <c>claude -p "/compact …" --resume &lt;id&gt;</c> in the chat's folder, which writes
/// the compact boundary and the summary into the conversation as the tab's own <c>/compact</c> does (spike, 2026-10-08).
/// It runs as VS Code's Claude Code (<c>CLAUDE_CODE_ENTRYPOINT</c>), which keeps the chat in VS Code's session list, and
/// with no model given: Claude Code's own, as for a chat in a new tab. Its JSON result says whether it compacted; one
/// that fails says why there or on standard error. It runs with hooks off: its SessionStart and SessionEnd carry the
/// chat's own id, and would tell CodeSwitchX that the chat ended while its tab still runs.
/// </summary>
/// <param name="findClaude">The <c>claude.exe</c> to run (<see cref="ClaudeCliLocator"/>); null when none is installed.</param>
public sealed class ChatCompactor(IBrainProcessLauncher launcher, Func<string?> findClaude, TimeProvider time, ILogger<ChatCompactor> logger)
    : IChatCompactor
{
    /// <summary>How long a compaction may take: a short chat takes about 15 seconds, a long one a minute or two.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    public Task CompactAsync(string sessionId, string folder, string? keep, CancellationToken ct) =>
        RunAsync(sessionId, folder, Command(keep), "compact", "Compacted", "Compacting", ct);

    public Task NameAsync(string sessionId, string folder, string name, CancellationToken ct) =>
        RunAsync(sessionId, folder, $"/rename {OneLine(name)}", "name", "Named", "Naming", ct);

    /// <summary>Runs the command on the conversation; returns once Claude Code has done it.</summary>
    /// <param name="what">What the command does to the chat, for what is said: "compact".</param>
    private async Task RunAsync(string sessionId, string folder, string command, string what, string done, string doing, CancellationToken ct)
    {
        var claude = findClaude() ?? throw new YardActionException($"Claude Code is not installed where CodeSwitchX looks for it, so it cannot {what} the chat.");
        var arguments = new[] { "-p", command, "--resume", sessionId, "--output-format", "json", "--settings", ClaudeCliBrain.NoHooks };
        IBrainProcess process;
        try
        {
            process = launcher.Start(claude, arguments, folder, new Dictionary<string, string?> { ["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode" });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new YardActionException($"Claude Code did not start to {what} the chat: {ex.Message}");
        }

        using (process)
        {
            process.CloseInput(); // it reads nothing: the command is its prompt
            var lines = new List<string>();
            using var limit = new CancellationTokenSource(Limit, time);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            try
            {
                await foreach (var line in process.Lines.ReadAllAsync(both.Token).ConfigureAwait(false))
                {
                    lines.Add(line);
                }

                await process.Exited.WaitAsync(both.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                logger.LogWarning("{Doing} chat {Id} took longer than {Limit}; stopped", doing, sessionId, Limit);
                throw new YardActionException($"{doing} the chat took longer than {Limit.TotalMinutes:0} minutes and was stopped; it is as it was.");
            }

            var result = lines.Select(ResultOf).LastOrDefault(r => r is not null);
            if (result is { Failed: false })
            {
                logger.LogInformation("{Done} chat {Id}", done, sessionId);
                return;
            }

            var why = result?.Text is { Length: > 0 } text ? text : LastLine(process.ErrorTail) ?? "it gave no reason";
            logger.LogWarning("{Doing} chat {Id} failed: {Why}", doing, sessionId, why);
            throw new YardActionException($"Claude Code did not {what} the chat: {why}");
        }
    }

    /// <summary>The command, with what to keep on one line: <c>/compact keep the test plan</c>.</summary>
    internal static string Command(string? keep) => OneLine(keep) is { Length: > 0 } words ? $"/compact {words}" : "/compact";

    private static string OneLine(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record Result(bool Failed, string? Text);

    /// <summary>The result line (<c>"type":"result"</c>); null for any other line.</summary>
    private static Result? ResultOf(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "result")
            {
                return null;
            }

            var failed = !root.TryGetProperty("is_error", out var error) || error.ValueKind != JsonValueKind.False;
            var text = root.TryGetProperty("result", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString() : null;
            if (failed && string.IsNullOrWhiteSpace(text) && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                text = string.Join(" ", errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            }

            return new Result(failed, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
