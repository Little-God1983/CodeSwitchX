using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// Turns the relay envelope (or a bare Claude Code hook payload) into a <see cref="HookEvent"/>.
/// Tolerant by design: unknown fields are ignored, unknown events keep a null signal, bad JSON yields null.
/// </summary>
public static class HookEnvelopeParser
{
    private static readonly HashSet<string> InformationalNotifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth_success",
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
            return new HookEvent
            {
                SessionId = sessionId,
                EventName = eventName,
                Signal = SignalFor(eventName, notificationType),
                At = receivedAt,
                Cwd = GetString(payload, "cwd"),
                TranscriptPath = GetString(payload, "transcript_path"),
                ToolName = GetString(payload, "tool_name"),
                ToolUseId = GetString(payload, "tool_use_id"),
                NotificationType = notificationType,
                Message = GetString(payload, "message") ?? GetString(payload, "title"),
                Prompt = GetString(payload, "prompt"),
                Model = GetString(payload, "model"),
                Source = GetString(payload, "source") ?? GetString(payload, "reason"),
                RelayPid = relayPid,
                ParentChain = chain,
                RawJson = payload.GetRawText(),
            };
        }
    }

    public static SessionSignal? SignalFor(string eventName, string? notificationType) => eventName switch
    {
        "SessionStart" => SessionSignal.SessionStart,
        "UserPromptSubmit" => SessionSignal.PromptSubmit,
        "PreToolUse" or "PostToolUse" => SessionSignal.ToolUse,
        "PermissionRequest" => SessionSignal.Notification,
        "Notification" when notificationType is null || !InformationalNotifications.Contains(notificationType) => SessionSignal.Notification,
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
