using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>The Yard's side of the panel's Changes tab (#444): the "N changed" pill opens it, and each git round keeps it current.</summary>
public partial class YardGitHubTests
{
    private void GitSays(string status) =>
        _gitRunner = args => args.StartsWith("status", StringComparison.Ordinal) ? status : string.Empty;

    private const string OneEdit = "1 .M N... 100644 100644 100644 aaaa aaaa a.txt\n";

    [Fact]
    public async Task The_changed_pill_opens_the_panel_on_the_lines_files()
    {
        GitSays(OneEdit + "? b.txt\n");
        var (yard, app) = await SettledYard();
        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.DirtyCount.ShouldBe(2);

        yard.OpenChangesCommand.Execute(line);

        var panel = yard.GitHubPanel.ShouldNotBeNull();
        panel.IsChangesShown.ShouldBeTrue();
        panel.Changes.ShouldNotBeNull().Rows.Select(r => r.DisplayPath).ShouldBe(["a.txt", "b.txt"]);
    }

    [Fact]
    public async Task A_git_round_keeps_the_open_tab_current()
    {
        GitSays(OneEdit);
        var (yard, app) = await SettledYard();
        yard.OpenChangesCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var changes = yard.GitHubPanel.ShouldNotBeNull().Changes.ShouldNotBeNull();
        await Eventually(() => changes.Rows.Count == 1);
        var rows = changes.Rows;

        await yard.RefreshGitAsync(CancellationToken.None);

        changes.Rows.ShouldBeSameAs(rows, "the same files read again keep the rows, and the list's scroll position");

        GitSays(OneEdit + "? c.txt\n");
        await yard.RefreshGitAsync(CancellationToken.None);

        await Eventually(() => changes.Rows.Count == 2);
        changes.Rows[1].DisplayPath.ShouldBe("c.txt");
    }

    [Fact]
    public async Task The_line_itself_still_opens_on_a_GitHub_list()
    {
        GitSays(OneEdit);
        var (yard, app) = await SettledYard();

        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);

        var panel = yard.GitHubPanel.ShouldNotBeNull();
        panel.IsChangesShown.ShouldBeFalse();
        panel.Shown.Kind.ShouldBe(GitHubItemKind.Issue, "no pull requests are open");
        panel.Changes.ShouldNotBeNull("its tab is there to pick");
    }

    [Fact]
    public async Task A_line_not_on_GitHub_opens_nothing()
    {
        GitSays(OneEdit);
        var (yard, app) = await SettledYard(withGitHub: false);

        yard.OpenChangesCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);

        yard.GitHubPanel.ShouldBeNull();
    }
}
