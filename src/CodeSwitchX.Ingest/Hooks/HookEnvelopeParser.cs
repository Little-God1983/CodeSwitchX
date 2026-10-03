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
    /// Notification types that mean Claude is blocked on the user. idle_prompt, which fires 60 s after a finished turn,
    /// has its own signal; treating it as Waiting would turn every finished chat amber a minute later. A missing type
    /// (older Claude Code) still counts as Waiting.
    /// </summary>
    private static readonly HashSet<string> NeedsUserNotifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "permission_prompt",
        "elicitation_dialog",
        "elicitation_url_dialog", // the same MCP elicitation, waiting for the user to open a link
        "agent_needs_input", // an in-session dialog, or an agent blocked in the background-agents view; the chat's next own event ends it
    };

    /// <summary>
    /// Notification types that carry no state: the chat is neither working nor waiting because of them. A worker of an agent
    /// team is a chat of its own with its own hooks, so its permission prompt turns its own row amber, not the lead's.
    /// </summary>
    private static readonly HashSet<string> InformationalNotifications = new(StringComparer.OrdinalIgnoreCase) { "auth_success", "worker_permission_prompt" };

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
            string? toolInputHash = null;
            string? projectDir = null;
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
                toolInputHash = GetString(root, "toolInputHash");
                projectDir = GetString(root, "projectDir");
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
            var (signal, informational) = Classify(eventName, notificationType, source);
            return new HookEvent
            {
                SessionId = sessionId,
                EventName = eventName,
                Signal = signal,
                Informational = informational,
                At = receivedAt,
                Cwd = GetString(payload, "cwd"),
                TranscriptPath = GetString(payload, "transcript_path"),
                ToolName = GetString(payload, "tool_name"),
                ToolUseId = GetString(payload, "tool_use_id"),
                ToolInputHash = toolInputHash,
                ProjectDir = projectDir,
                AgentId = GetString(payload, "agent_id"),
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

    public static SessionSignal? SignalFor(string eventName, string? notificationType, string? source = null) =>
        Classify(eventName, notificationType, source).Signal;

    /// <summary>
    /// The signal an event carries, or that it carries none on purpose (<c>Informational</c>): SessionStart after
    /// compaction, a notification that needs no one, SubagentStop. An event or notification type nobody mapped is
    /// neither, and the engine logs it: a new type may be a dialog the chat should have gone Waiting for.
    /// </summary>
    /// <param name="source">For SessionStart: startup, resume, clear or compact. Compaction happens in the middle of a turn, so it keeps the state.</param>
    internal static (SessionSignal? Signal, bool Informational) Classify(string eventName, string? notificationType, string? source) => eventName switch
    {
        "SessionStart" when string.Equals(source, "compact", StringComparison.OrdinalIgnoreCase) => (null, true),
        "SessionStart" => (SessionSignal.SessionStart, false),
        "UserPromptSubmit" => (SessionSignal.PromptSubmit, false),
        "PreToolUse" or "PostToolUse" => (SessionSignal.ToolUse, false),
        "PermissionRequest" => (SessionSignal.Notification, false),
        "Notification" when notificationType is null || NeedsUserNotifications.Contains(notificationType) => (SessionSignal.Notification, false),
        "Notification" when string.Equals(notificationType, "idle_prompt", StringComparison.OrdinalIgnoreCase) => (SessionSignal.IdlePrompt, false),
        "Notification" when notificationType is not null && InformationalNotifications.Contains(notificationType) => (null, true),
        // A turn that ends on an API error (usage limit, overload, prompt too long) sends StopFailure instead of Stop.
        "Stop" or "StopFailure" => (SessionSignal.Stop, false),
        "SessionEnd" => (SessionSignal.SessionEnd, false),
        // Fires while the parent's turn goes on, which the parent's own PostToolUse or Stop ends (decisions.md).
        "SubagentStop" => (null, true),
        _ => (null, false),
    };

    /// <summary>The string property, or null when it is missing, not a string, or holds a lone surrogate escape (<see cref="JsonStrings.TryRead"/>).</summary>
    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? JsonStrings.TryRead(value) : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;
}
