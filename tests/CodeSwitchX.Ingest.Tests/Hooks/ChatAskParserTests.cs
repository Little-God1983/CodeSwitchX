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
}
