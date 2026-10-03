using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

/// <summary>What the app says to have the user confirm an allow the brain proposed (#108): what it allows, where, and its risks.</summary>
public sealed class PermissionReadBackTests
{
    private static ChatAsk Prompt(string tool, string wants, string subject, string? agent = null, params PermissionRisk[] risks) =>
        new("p1", new HookEvent { SessionId = "s1", EventName = "PermissionRequest", At = DateTimeOffset.UtcNow, ToolName = tool, AgentId = agent },
            [], new ChatPermission(tool, wants, subject, agent is null ? null : "Explore", Risks: risks.Length > 0 ? risks : null));

    [Theory]
    [InlineData("Bash", "run a command", "npm test", null, "Run npm test in CodeSwitchX? Say yes.")]
    [InlineData("Edit", "edit a file", @"E:\Repos\App\App.xaml.cs", "a1", @"Let its Explore sub-agent edit E:\Repos\App\App.xaml.cs in CodeSwitchX? Say yes.")]
    [InlineData("WebSearch", "search the web", "dotnet 10", null, "Search the web for dotnet 10 in CodeSwitchX? Say yes.")]
    [InlineData("mcp__github__create_issue", "use mcp__github__create_issue", "title: Bug", null, "Use mcp__github__create_issue with title: Bug in CodeSwitchX? Say yes.")]
    public void The_read_back_names_what_is_allowed_and_asks_for_the_yes(string tool, string wants, string subject, string? agent, string said)
    {
        PermissionReadBack.Of(Prompt(tool, wants, subject, agent), "CodeSwitchX").ShouldBe(said);
    }

    [Fact]
    public void What_is_risky_in_it_is_said_before_the_yes()
    {
        PermissionReadBack.Of(Prompt("Bash", "run a command", "rm -rf dist && git push", null, PermissionRisk.DeletesFiles, PermissionRisk.Pushes), "CodeSwitchX")
            .ShouldBe("Run rm -rf dist && git push in CodeSwitchX? It deletes files and pushes to a remote. Say yes.");
    }

    [Fact]
    public void A_long_command_is_cut_said_to_be_and_its_risks_said_even_when_they_are_in_the_cut_part()
    {
        var command = string.Join(" && ", Enumerable.Range(1, 8).Select(i => $"dotnet test tests/Project{i}")) + " && rm -rf dist";

        var said = PermissionReadBack.Of(Prompt("Bash", "run a command", command, null, PermissionRisk.DeletesFiles), "CodeSwitchX");

        said.ShouldBe("Run " + command[..(PermissionReadBack.MaxSaid - 1)].TrimEnd() + "… (the rest is on the card) in CodeSwitchX? It deletes files. Say yes.");
    }

    [Fact]
    public void A_cut_never_splits_an_emoji_and_an_unknown_workspace_is_left_out()
    {
        var command = new string('x', PermissionReadBack.MaxSaid - 2) + "😀 tail";

        var said = PermissionReadBack.Of(Prompt("Bash", "run a command", command), null);

        said.ShouldBe("Run " + new string('x', PermissionReadBack.MaxSaid - 2) + "… (the rest is on the card)? Say yes.");
    }
}
