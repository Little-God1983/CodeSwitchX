using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// Reads what a chat asks from what its relay hands over. A question is the <c>PreToolUse</c> of its
/// <c>AskUserQuestion</c>: <c>tool_input.questions[]</c> with <c>question</c>, <c>header</c>, <c>options[]</c> (<c>label</c>,
/// <c>description</c>) and <c>multiSelect</c>. A permission prompt is a <c>PermissionRequest</c>: <c>tool_name</c>,
/// <c>tool_input</c>, and <c>agent_id</c> / <c>agent_type</c> when a sub-agent asks; it has no <c>tool_use_id</c>. Tolerant
/// like <see cref="HookEnvelopeParser"/>: anything else yields null, and VS Code asks it.
/// </summary>
public static class ChatAskParser
{
    public const string QuestionTool = "AskUserQuestion";

    public const string PermissionEvent = "PermissionRequest";

    /// <summary>A shell command, a file, a URL or a tool's input is cut here on the card.</summary>
    internal const int MaxSubjectChars = 600;

    /// <summary>A tool's input shown as its excerpt, for a tool the card knows nothing of.</summary>
    internal const int MaxExcerptChars = 160;

    /// <summary>
    /// Prompts left to VS Code: a question asks there unless its own hook takes it, and a plan to approve has to be read
    /// in its tab.
    /// </summary>
    private static readonly HashSet<string> LeftToVsCode = new(StringComparer.Ordinal) { QuestionTool, "ExitPlanMode" };

    /// <summary>The ask in the relay's envelope (or a bare payload); null when it holds nothing this can show.</summary>
    public static ChatAsk? Parse(string json, DateTimeOffset receivedAt)
    {
        var step = HookEnvelopeParser.Parse(json, receivedAt);
        if (step is { EventName: PermissionEvent })
        {
            return Permission(json, step);
        }

        if (step is not { ToolName: QuestionTool })
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var payload = root.TryGetProperty("payload", out var inner) ? inner : root;
        if (!payload.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var questions = new List<ChatQuestion>();
        foreach (var item in list.EnumerateArray())
        {
            if (String(item, "question") is not { Length: > 0 } text)
            {
                return null; // a question it cannot show would be answered unseen: VS Code asks them all
            }

            var options = new List<ChatQuestionOption>();
            if (item.TryGetProperty("options", out var optionList) && optionList.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionList.EnumerateArray())
                {
                    if (String(option, "label") is { Length: > 0 } label)
                    {
                        options.Add(new ChatQuestionOption(label, String(option, "description")));
                    }
                }
            }

            var multi = item.TryGetProperty("multiSelect", out var flag) && flag.ValueKind == JsonValueKind.True;
            questions.Add(new ChatQuestion(text, String(item, "header"), options, multi));
        }

        return questions.Count == 0 ? null : new ChatAsk(step.ToolUseId ?? Guid.NewGuid().ToString("N"), ChatAskKind.Question, step, questions);
    }

    /// <summary>The permission prompt: what the tool wants and on what, as the card shows it.</summary>
    private static ChatAsk? Permission(string json, HookEvent step)
    {
        if (step.ToolName is not { Length: > 0 } tool || LeftToVsCode.Contains(tool))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var payload = root.TryGetProperty("payload", out var inner) ? inner : root;
        var input = payload.TryGetProperty("tool_input", out var given) && given.ValueKind == JsonValueKind.Object ? given : default;
        var (wants, subject) = tool switch
        {
            "Bash" or "PowerShell" => ("run a command", String(input, "command")),
            "Edit" or "MultiEdit" => ("edit a file", String(input, "file_path")),
            "Write" => ("write a file", String(input, "file_path")),
            "NotebookEdit" => ("edit a notebook", String(input, "notebook_path")),
            "WebFetch" => ("fetch a web page", String(input, "url")),
            "WebSearch" => ("search the web", String(input, "query")),
            _ => ($"use {tool}", Excerpt(input)),
        };

        // Without what it is about, a known tool shows as any other: its name and a piece of its input.
        if (subject is not { Length: > 0 })
        {
            (wants, subject) = ($"use {tool}", Excerpt(input));
        }

        var agent = step.AgentId is null ? null : String(payload, "agent_type") is { Length: > 0 } type ? type : "sub-agent";
        return new ChatAsk(Guid.NewGuid().ToString("N"), ChatAskKind.Permission, step, [],
            new ChatPermission(tool, wants, Cut(subject, MaxSubjectChars), agent));
    }

    /// <summary>The tool's input as compact JSON, cut short: "{"path":"src","pattern":"TODO"}"; empty for none.</summary>
    private static string Excerpt(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object ? Cut(input.GetRawText().ReplaceLineEndings(" "), MaxExcerptChars) : "";

    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[max - 2]) ? max - 2 : max - 1; // never half an emoji
        return text[..length].TrimEnd() + "…";
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? JsonStrings.TryRead(value)?.Trim() : null;
}
