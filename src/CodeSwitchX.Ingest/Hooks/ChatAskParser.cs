using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// Reads what a chat asks from the relayed <c>PreToolUse</c> of its <c>AskUserQuestion</c>:
/// <c>tool_input.questions[]</c> with <c>question</c>, <c>header</c>, <c>options[]</c> (<c>label</c>, <c>description</c>) and
/// <c>multiSelect</c>. Tolerant like <see cref="HookEnvelopeParser"/>: anything else yields null, and VS Code asks it.
/// </summary>
public static class ChatAskParser
{
    public const string QuestionTool = "AskUserQuestion";

    /// <summary>The ask in the relay's envelope (or a bare payload); null when it holds no question this can show.</summary>
    public static ChatAsk? Parse(string json, DateTimeOffset receivedAt)
    {
        if (HookEnvelopeParser.Parse(json, receivedAt) is not { ToolName: QuestionTool } step)
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

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? JsonStrings.TryRead(value)?.Trim() : null;
}
