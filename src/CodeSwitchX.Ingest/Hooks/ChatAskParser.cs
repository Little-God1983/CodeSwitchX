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

        return questions.Count == 0 ? null : new ChatAsk(step.ToolUseId ?? Guid.NewGuid().ToString("N"), step, questions);
    }

    /// <summary>
    /// The permission prompt: what the tool wants and on what, as the card shows it, and all of its input where that says
    /// more than what it is on (an edit's change, a file's content). Allow allows all of it, so nothing is cut; a prompt
    /// whose input did not come whole (too big for the relay to hand over) is left to VS Code, whose prompt shows it.
    /// </summary>
    private static ChatAsk? Permission(string json, HookEvent step)
    {
        if (step.ToolName is not { Length: > 0 } tool || LeftToVsCode.Contains(tool))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var payload = root.TryGetProperty("payload", out var inner) ? inner : root;
        if (!payload.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var all = Listing(input);
        var (wants, subject, details) = tool switch
        {
            "Bash" or "PowerShell" => ("run a command", String(input, "command"), null),
            "Edit" or "MultiEdit" => ("edit a file", String(input, "file_path"), all),
            "Write" => ("write a file", String(input, "file_path"), all),
            "NotebookEdit" => ("edit a notebook", String(input, "notebook_path"), all),
            "Read" => ("read a file", String(input, "file_path"), null),
            "WebFetch" => ("fetch a web page", String(input, "url"), null),
            "WebSearch" => ("search the web", String(input, "query"), null),
            _ => ($"use {tool}", all, (string?)null),
        };

        // Without what it is about, a known tool shows as any other: its name and its input.
        if (subject is not { Length: > 0 })
        {
            (wants, subject, details) = ($"use {tool}", all, null);
        }

        var risks = (tool, subject == all) switch
        {
            (_, true) => [],
            ("Bash" or "PowerShell", _) => PermissionRisks.OfCommand(subject, step.Cwd),
            ("Edit" or "MultiEdit" or "NotebookEdit", _) => PermissionRisks.OfWrite(subject, step.Cwd),
            ("Write", _) => PermissionRisks.OfWrite(subject, step.Cwd, emptiesIt: Empties(input, subject)),
            _ => (IReadOnlyList<PermissionRisk>)[],
        };

        var agent = step.AgentId is null ? null : String(payload, "agent_type") is { Length: > 0 } type ? type : "sub-agent";
        return new ChatAsk(Guid.NewGuid().ToString("N"), step, [], new ChatPermission(tool, wants, subject, agent, details, risks.Count > 0 ? risks : null));
    }

    /// <summary>A Write of nothing (or only blanks) over a file that is there.</summary>
    private static bool Empties(JsonElement input, string path) =>
        input.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
        && string.IsNullOrWhiteSpace(content.GetString()) && File.Exists(path);

    /// <summary>
    /// The tool's input to read: a line per field, "name: value", text as it is (paths without doubled backslashes, an
    /// edit's lines as lines), anything else as compact JSON.
    /// </summary>
    private static string Listing(JsonElement input) => string.Join('\n', input.EnumerateObject().Select(field =>
        $"{field.Name}: {(field.Value.ValueKind == JsonValueKind.String ? JsonStrings.TryRead(field.Value) ?? "" : field.Value.GetRawText())}"));

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? JsonStrings.TryRead(value)?.Trim() : null;
}
