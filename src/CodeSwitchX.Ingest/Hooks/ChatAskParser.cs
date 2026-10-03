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

        // Outside is outside the chat's project, not the folder it moved to; a relay older than #107 sends only the latter.
        var folder = step.ProjectDir ?? step.Cwd;
        var risks = (tool, subject == all) switch
        {
            (_, true) => [],
            ("Bash", _) => PermissionRisks.OfCommand(subject, folder, step.Cwd),
            ("PowerShell", _) => PermissionRisks.OfCommand(subject, folder, step.Cwd, ShellDialect.PowerShell),
            ("Edit" or "MultiEdit" or "NotebookEdit", _) => PermissionRisks.OfWrite(subject, folder, cwd: step.Cwd),
            ("Write", _) => PermissionRisks.OfWrite(subject, folder, Empties(input, subject), step.Cwd),
            _ => (IReadOnlyList<PermissionRisk>)[],
        };

        var agent = step.AgentId is null ? null : String(payload, "agent_type") is { Length: > 0 } type ? type : "sub-agent";
        // Only a relay that hands the rule back to Claude Code gets "Always allow": an older one would allow once, keep nothing.
        return new ChatAsk(Guid.NewGuid().ToString("N"), step, [], new ChatPermission(tool, wants, subject, agent, details, risks.Count > 0 ? risks : null),
            step.RelayKeepsRules ? Suggestions(payload) : null);
    }

    /// <summary>
    /// The standing rules Claude Code suggests with the prompt (<c>permission_suggestions</c>), each kept as it came and
    /// worded for its button and its tooltip. One whose effect cannot be worded truthfully (a kind not known here, a rule
    /// that denies, a mode other than accepting edits, such as bypassing every prompt) is not offered. Null when none is.
    /// </summary>
    private static IReadOnlyList<ChatPermissionSuggestion>? Suggestions(JsonElement payload)
    {
        if (!payload.TryGetProperty("permission_suggestions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var suggestions = list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => Worded(item) is { } worded
                ? new ChatPermissionSuggestion(item.GetRawText(), worded.Label, SavedWhere(String(item, "destination")), worded.Effect)
                : null)
            .OfType<ChatPermissionSuggestion>()
            .ToList();
        return suggestions.Count > 0 ? suggestions : null;
    }

    /// <summary>
    /// The button's words and the tooltip's: "Always allow npm test", "Allow all edits", "Let the chat work in E:\Data"; null
    /// for what is not offered.
    /// </summary>
    private static (string Label, string Effect)? Worded(JsonElement item)
    {
        var session = String(item, "destination") == "session";
        switch (String(item, "type"))
        {
            case "addRules" when String(item, "behavior") == "allow" && item.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array:
                var said = rules.EnumerateArray().Select(RuleSaid).ToList();
                // A rule for this session only is no "always": "Allow npm test for this session".
                return said.Count == 0 || said.Contains(null) ? null
                    : ((session ? "Allow " : "Always allow ") + string.Join(", ", said), session
                        ? "Claude Code keeps the rule until this session ends, and asks again after that."
                        : "Claude Code keeps the rule and does not ask for this again.");
            case "setMode" when String(item, "mode") == "acceptEdits":
                // Accept-edits holds only in the chat's working folders.
                return ("Allow all edits", "The chat edits files in its folders without asking " + (session ? "until this session ends" : "from now on")
                    + "; edits elsewhere and commands still ask.");
            case "addDirectories" when item.TryGetProperty("directories", out var directories) && directories.ValueKind == JsonValueKind.Array:
                // A working folder only stops read prompts: edits there still ask unless accept-edits is on.
                var folders = directories.EnumerateArray().Select(JsonStrings.TryRead).ToList();
                return folders.Count == 0 || folders.Any(string.IsNullOrWhiteSpace) ? null
                    : ("Let the chat work in " + string.Join(", ", folders),
                        "The chat may read files there without asking " + (session ? "until this session ends" : "from now on") + "; edits and commands can still ask.");
            default:
                return null;
        }
    }

    /// <summary>A rule as its button says it: a command for Bash and PowerShell, "Edit of src/**" for another tool, or the tool alone.</summary>
    private static string? RuleSaid(JsonElement rule)
    {
        if (String(rule, "toolName") is not { Length: > 0 } tool)
        {
            return null;
        }

        var content = String(rule, "ruleContent");
        if (content is not { Length: > 0 })
        {
            return $"every use of {tool}";
        }

        // Claude Code's prefix rule "npm test:*" is said as people say it.
        var said = content.EndsWith(":*", StringComparison.Ordinal) && content.Length > 2 ? $"{content[..^2]} and anything after it" : content;
        return tool is "Bash" or "PowerShell" ? said : $"{tool} of {said}";
    }

    /// <summary>Where Claude Code keeps the rule, as said after the label.</summary>
    private static string SavedWhere(string? destination) => destination switch
    {
        "localSettings" => "in this folder, just you",
        "projectSettings" => "in this folder, for everyone on the project",
        "userSettings" => "in every folder",
        "session" => "for this session",
        { Length: > 0 } other => $"({other})",
        _ => "",
    };

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
