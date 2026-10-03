using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Hooks;

namespace CodeSwitchX.Ingest.Tests.Hooks;

public sealed class ChatAskParserTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static string Envelope(string toolInput, string tool = "AskUserQuestion") => $$$"""
        {"event":"PreToolUse","relayPid":7,"parentChain":[{"pid":9,"name":"claude.exe"}],
         "payload":{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"{{{tool}}}","tool_use_id":"toolu_1","agent_id":"a1",
                    "cwd":"E:\\Repo","tool_input":{{{toolInput}}}}}
        """;

    [Fact]
    public void A_question_is_read_with_its_options_and_the_step_that_asks_it()
    {
        var ask = ChatAskParser.Parse(Envelope("""
            {"questions":[{"question":" Which fruit? ","header":"Fruit","options":[{"label":"Apple","description":"red"},{"label":"Banana"}],"multiSelect":false},
                          {"question":"Which colours?","options":[{"label":"Red"},{"label":""}],"multiSelect":true}]}
            """), At).ShouldNotBeNull();

        ask.Id.ShouldBe("toolu_1");
        ask.Kind.ShouldBe(ChatAskKind.Question);
        (ask.SessionId, ask.Step.AgentId, ask.Step.Cwd, ask.Step.RelayPid, ask.At).ShouldBe(("s1", "a1", @"E:\Repo", 7, At));
        ask.Questions.Count.ShouldBe(2);
        ask.Questions[0].ShouldSatisfyAllConditions(
            q => q.Text.ShouldBe("Which fruit?"),
            q => q.Header.ShouldBe("Fruit"),
            q => q.MultiSelect.ShouldBeFalse(),
            q => q.Options.ShouldBe([new ChatQuestionOption("Apple", "red"), new ChatQuestionOption("Banana", null)]));
        ask.Questions[1].MultiSelect.ShouldBeTrue();
        ask.Questions[1].Options.ShouldBe([new ChatQuestionOption("Red", null)], "an option without a label cannot be shown or picked");
    }

    [Theory]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":[{"question":"","options":[]}]}""")]
    [InlineData("""{"questions":"Which?"}""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    public void Anything_it_cannot_show_is_left_to_VS_Code(string toolInput)
    {
        ChatAskParser.Parse(Envelope(toolInput), At).ShouldBeNull();
    }

    [Fact]
    public void Another_tool_is_no_question()
    {
        ChatAskParser.Parse(Envelope("""{"questions":[{"question":"Q?"}]}""", tool: "Bash"), At).ShouldBeNull();
    }

    [Fact]
    public void A_question_without_options_is_asked_for_the_user_s_own_words()
    {
        ChatAskParser.Parse(Envelope("""{"questions":[{"question":"Which name?"}]}"""), At).ShouldNotBeNull()
            .Questions.ShouldHaveSingleItem().Options.ShouldBeEmpty();
    }

    /// <summary>A PermissionRequest as the lab saw it (extension 2.1.287): no tool_use_id; agent_id and agent_type for a sub-agent.</summary>
    private static string Permission(string tool, string toolInput, string agent = "") => $$$"""
        {"event":"PermissionRequest","relayPid":7,"parentChain":[],
         "payload":{"session_id":"s1","hook_event_name":"PermissionRequest","tool_name":"{{{tool}}}","cwd":"E:\\Repo",{{{agent}}}
                    "tool_input":{{{toolInput}}},
                    "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow","destination":"localSettings"}]}}
        """;

    [Theory]
    [InlineData("Bash", """{"command":"npm test","description":"Run the tests"}""", "run a command", "npm test")]
    [InlineData("PowerShell", """{"command":"Remove-Item build -Recurse"}""", "run a command", "Remove-Item build -Recurse")]
    [InlineData("Edit", """{"file_path":"E:\\Repo\\App.cs","old_string":"a","new_string":"b"}""", "edit a file", @"E:\Repo\App.cs")]
    [InlineData("Write", """{"file_path":"E:\\Repo\\new.txt","content":"x"}""", "write a file", @"E:\Repo\new.txt")]
    [InlineData("NotebookEdit", """{"notebook_path":"E:\\Repo\\a.ipynb"}""", "edit a notebook", @"E:\Repo\a.ipynb")]
    [InlineData("WebFetch", """{"url":"https://github.com/x","prompt":"read it"}""", "fetch a web page", "https://github.com/x")]
    [InlineData("mcp__github__create_issue", """{"title":"Bug"}""", "use mcp__github__create_issue", """{"title":"Bug"}""")]
    [InlineData("Bash", """{"description":"no command"}""", "use Bash", """{"description":"no command"}""")]
    public void A_permission_prompt_is_read_as_what_the_tool_wants_and_on_what(string tool, string toolInput, string wants, string subject)
    {
        var ask = ChatAskParser.Parse(Permission(tool, toolInput), At).ShouldNotBeNull();

        ask.Kind.ShouldBe(ChatAskKind.Permission);
        ask.Questions.ShouldBeEmpty();
        (ask.SessionId, ask.Step.EventName, ask.Step.AgentId, ask.At).ShouldBe(("s1", "PermissionRequest", null, At));
        ask.Permission.ShouldBe(new ChatPermission(tool, wants, subject, null));
    }

    [Fact]
    public void A_sub_agent_s_permission_prompt_names_it()
    {
        var ask = ChatAskParser.Parse(Permission("Bash", """{"command":"npm test"}""", """ "agent_id":"a1","agent_type":"Explore", """), At).ShouldNotBeNull();

        ask.Step.AgentId.ShouldBe("a1");
        ask.Permission.ShouldNotBeNull().Agent.ShouldBe("Explore");
    }

    [Fact]
    public void Each_permission_prompt_gets_an_id_of_its_own()
    {
        // There is no tool_use_id to take it from.
        var first = ChatAskParser.Parse(Permission("Bash", """{"command":"ls"}"""), At).ShouldNotBeNull();
        var second = ChatAskParser.Parse(Permission("Bash", """{"command":"ls"}"""), At).ShouldNotBeNull();

        first.Id.ShouldNotBe(second.Id);
    }

    [Fact]
    public void A_long_command_is_cut_on_the_card()
    {
        var command = new string('x', 2000);

        var subject = ChatAskParser.Parse(Permission("Bash", $$"""{"command":"{{command}}"}"""), At).ShouldNotBeNull().Permission!.Subject;

        subject.Length.ShouldBe(ChatAskParser.MaxSubjectChars);
        subject.ShouldEndWith("…");
    }

    [Theory]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    public void A_question_and_a_plan_to_approve_are_left_to_VS_Code(string tool)
    {
        ChatAskParser.Parse(Permission(tool, """{"plan":"…"}"""), At).ShouldBeNull();
    }
}
