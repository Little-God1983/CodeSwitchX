using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Hooks;

namespace CodeSwitchX.Ingest.Tests.Hooks;

public class HookEnvelopeParserTests
{
    private static readonly DateTimeOffset Received = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static string Envelope(string eventName, string payloadJson, string parentChain = "[]") =>
        $$"""
        {"event":"{{eventName}}","receivedAtUtc":"2026-09-23T09:59:59.900Z","relayPid":4242,"parentChain":{{parentChain}},"payload":{{payloadJson}}}
        """;

    [Fact]
    public void Pre_tool_use_maps_to_ToolUse_with_tool_name_cwd_and_transcript()
    {
        var json = Envelope("PreToolUse",
            """{"session_id":"abc","transcript_path":"C:\\Users\\me\\.claude\\projects\\x\\abc.jsonl","cwd":"C:\\Repo\\App","hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"dotnet build"},"tool_use_id":"toolu_1"}""",
            """[{"pid":100,"name":"cmd.exe"},{"pid":200,"name":"claude.exe"}]""");

        var e = HookEnvelopeParser.Parse(json, Received).ShouldNotBeNull();

        e.SessionId.ShouldBe("abc");
        e.EventName.ShouldBe("PreToolUse");
        e.Signal.ShouldBe(SessionSignal.ToolUse);
        e.ToolName.ShouldBe("Bash");
        e.ToolUseId.ShouldBe("toolu_1");
        e.Cwd.ShouldBe(@"C:\Repo\App");
        e.TranscriptPath.ShouldBe(@"C:\Users\me\.claude\projects\x\abc.jsonl");
        e.At.ShouldBe(Received);
        e.RelayPid.ShouldBe(4242);
        e.ParentChain.ShouldBe([new ProcessRef(100, "cmd.exe"), new ProcessRef(200, "claude.exe")]);
        e.RawJson.ShouldContain("\"tool_name\"");
    }

    [Theory]
    [InlineData("SessionStart", SessionSignal.SessionStart)]
    [InlineData("UserPromptSubmit", SessionSignal.PromptSubmit)]
    [InlineData("PreToolUse", SessionSignal.ToolUse)]
    [InlineData("PostToolUse", SessionSignal.ToolUse)]
    [InlineData("Notification", SessionSignal.Notification)]
    [InlineData("PermissionRequest", SessionSignal.Notification)]
    [InlineData("Stop", SessionSignal.Stop)]
    [InlineData("SessionEnd", SessionSignal.SessionEnd)]
    public void Known_events_map_to_signals(string eventName, SessionSignal expected)
    {
        HookEnvelopeParser.SignalFor(eventName, null).ShouldBe(expected);
    }

    [Theory]
    [InlineData("SubagentStop")]
    [InlineData("PreCompact")]
    [InlineData("SomethingNew")]
    public void Events_without_a_state_meaning_have_no_signal(string eventName)
    {
        HookEnvelopeParser.SignalFor(eventName, null).ShouldBeNull();
    }

    [Theory]
    [InlineData("permission_prompt", SessionSignal.Notification)]
    [InlineData("idle_prompt", SessionSignal.Notification)]
    [InlineData("elicitation_dialog", SessionSignal.Notification)]
    [InlineData("future_type", SessionSignal.Notification)]
    [InlineData("auth_success", null)]
    public void Informational_notifications_do_not_mean_waiting(string type, SessionSignal? expected)
    {
        HookEnvelopeParser.SignalFor("Notification", type).ShouldBe(expected);
    }

    [Fact]
    public void Unknown_event_names_still_parse_with_a_null_signal()
    {
        var e = HookEnvelopeParser.Parse(Envelope("SomethingNew", """{"session_id":"abc","hook_event_name":"SomethingNew","extra":{"deep":[1,2,3]}}"""), Received);

        e.ShouldNotBeNull();
        e.EventName.ShouldBe("SomethingNew");
        e.Signal.ShouldBeNull();
    }

    [Fact]
    public void Prompt_notification_and_session_fields_are_extracted()
    {
        var prompt = HookEnvelopeParser.Parse(Envelope("UserPromptSubmit", """{"session_id":"abc","hook_event_name":"UserPromptSubmit","prompt":"Fix the build"}"""), Received)!;
        prompt.Prompt.ShouldBe("Fix the build");

        var note = HookEnvelopeParser.Parse(Envelope("Notification", """{"session_id":"abc","hook_event_name":"Notification","message":"Claude needs your permission to use Bash","notification_type":"permission_prompt"}"""), Received)!;
        note.Message.ShouldBe("Claude needs your permission to use Bash");
        note.NotificationType.ShouldBe("permission_prompt");

        var start = HookEnvelopeParser.Parse(Envelope("SessionStart", """{"session_id":"abc","hook_event_name":"SessionStart","source":"startup","model":"claude-sonnet-5"}"""), Received)!;
        start.Source.ShouldBe("startup");
        start.Model.ShouldBe("claude-sonnet-5");

        var end = HookEnvelopeParser.Parse(Envelope("SessionEnd", """{"session_id":"abc","hook_event_name":"SessionEnd","reason":"prompt_input_exit"}"""), Received)!;
        end.Source.ShouldBe("prompt_input_exit");
    }

    [Fact]
    public void The_payloads_own_event_name_wins_over_the_envelope()
    {
        var e = HookEnvelopeParser.Parse(Envelope("Stop", """{"session_id":"abc","hook_event_name":"PostToolUse","tool_name":"Read"}"""), Received)!;

        e.EventName.ShouldBe("PostToolUse");
        e.Signal.ShouldBe(SessionSignal.ToolUse);
    }

    [Fact]
    public void A_bare_claude_payload_is_accepted()
    {
        var e = HookEnvelopeParser.Parse("""{"session_id":"abc","hook_event_name":"Stop","cwd":"C:\\x"}""", Received).ShouldNotBeNull();

        e.Signal.ShouldBe(SessionSignal.Stop);
        e.RelayPid.ShouldBeNull();
        e.ParentChain.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"event":"Stop","payload":{"hook_event_name":"Stop"}}""")]
    [InlineData("""{"event":"Stop","payload":"string"}""")]
    public void Invalid_or_session_less_input_returns_null(string json)
    {
        HookEnvelopeParser.Parse(json, Received).ShouldBeNull();
    }
}
