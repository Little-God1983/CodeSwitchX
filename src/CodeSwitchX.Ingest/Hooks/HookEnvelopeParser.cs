using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// Turns the relay envelope (or a bare Claude Code hook payload) into a <see cref="HookEvent"/>.
/// Tolerant by design: unknown fields are ignored, unknown events keep a null signal, bad JSON yields null.
/// </summary>
public static class HookEnvelopeParser
{
    /// <summary>
    /// Notification types that mean Claude is blocked on the user. Everything else (auth_success, idle_prompt, which
    /// fires 60 s after a finished turn, and unknown future types) is informational: treating it as Waiting would turn
    /// every finished chat amber a minute later. A missing type (older Claude Code) still counts as Waiting.
    /// </summary>
    private static readonly HashSet<string> NeedsUserNotifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "permission_prompt",
        "elicitation_dialog",
    };

    public static HookEvent? Parse(string json, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement payload;
            string? envelopeEvent = null;
            int? relayPid = null;
            var chain = new List<ProcessRef>();

            if (root.TryGetProperty("payload", out var payloadElement))
            {
                if (payloadElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                payload = payloadElement;
                envelopeEvent = GetString(root, "event");
                relayPid = GetInt(root, "relayPid");
                if (root.TryGetProperty("parentChain", out var chainElement) && chainElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in chainElement.EnumerateArray())
                    {
                        var pid = GetInt(item, "pid");
                        var name = GetString(item, "name");
                        if (pid is { } p && name is not null)
                        {
                            chain.Add(new ProcessRef(p, name));
                        }
                    }
                }
            }
            else
            {
                payload = root;
            }

            var sessionId = GetString(payload, "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var eventName = GetString(payload, "hook_event_name") ?? envelopeEvent;
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return null;
            }

            var notificationType = GetString(payload, "notification_type");
            var source = GetString(payload, "source") ?? GetString(payload, "reason");
            return new HookEvent
            {
                SessionId = sessionId,
                EventName = eventName,
                Signal = SignalFor(eventName, notificationType, source),
                At = receivedAt,
                Cwd = GetString(payload, "cwd"),
                TranscriptPath = GetString(payload, "transcript_path"),
                ToolName = GetString(payload, "tool_name"),
                ToolUseId = GetString(payload, "tool_use_id"),
                NotificationType = notificationType,
                Message = GetString(payload, "message") ?? GetString(payload, "title"),
                Prompt = GetString(payload, "prompt"),
                Model = GetString(payload, "model"),
                Source = source,
                RelayPid = relayPid,
                ParentChain = chain,
                RawJson = payload.GetRawText(),
            };
        }
    }

    /// <param name="source">For SessionStart: startup, resume, clear or compact. Compaction happens in the middle of a turn, so it keeps the state.</param>
    public static SessionSignal? SignalFor(string eventName, string? notificationType, string? source = null) => eventName switch
    {
        "SessionStart" when string.Equals(source, "compact", StringComparison.OrdinalIgnoreCase) => null,
        "SessionStart" => SessionSignal.SessionStart,
        "UserPromptSubmit" => SessionSignal.PromptSubmit,
        "PreToolUse" or "PostToolUse" => SessionSignal.ToolUse,
        "PermissionRequest" => SessionSignal.Notification,
        "Notification" when notificationType is null || NeedsUserNotifications.Contains(notificationType) => SessionSignal.Notification,
        "Stop" => SessionSignal.Stop,
        "SessionEnd" => SessionSignal.SessionEnd,
        _ => null,
    };

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;
}
