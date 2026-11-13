using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>What a repository line's pills say (#426): "clean" only where nothing is left to do.</summary>
public class GitLineTests
{
    private static GitLine Line(int? dirty, GitBehind? behind) => new(null, "main", dirty, Behind: behind);

    [Theory]
    [InlineData(0, "clean")]
    [InlineData(1, "1 changed")]
    [InlineData(3, "3 changed")]
    [InlineData(null, null)]
    public void A_line_that_is_not_behind_says_clean_or_how_many_files_changed(int? dirty, string? label)
    {
        var line = Line(dirty, null);

        line.GitStateLabel.ShouldBe(label);
        line.BehindLabel.ShouldBeNull();
        line.GitStateText.ShouldBe(label);
    }

    [Fact]
    public void Behind_replaces_clean()
    {
        var line = Line(0, new GitBehind(12));

        line.GitStateLabel.ShouldBeNull("12 behind is not clean");
        line.BehindLabel.ShouldBe("12 behind");
        line.GitStateText.ShouldBe("12 behind");
    }

    [Fact]
    public void Changed_files_and_behind_show_side_by_side()
    {
        var line = Line(3, new GitBehind(12));

        line.GitStateLabel.ShouldBe("3 changed");
        line.BehindLabel.ShouldBe("12 behind");
        line.GitStateText.ShouldBe("3 changed, 12 behind");
    }

    [Fact]
    public void Behind_without_a_count_says_only_that()
    {
        var line = Line(0, new GitBehind(null));

        line.BehindLabel.ShouldBe("behind");
        line.GitStateText.ShouldBe("behind");
    }

    [Fact]
    public void A_line_git_could_not_read_still_says_it_is_behind()
    {
        var line = Line(null, new GitBehind(1));

        line.GitStateLabel.ShouldBeNull("a made-up \"clean\" would hide changes");
        line.BehindLabel.ShouldBe("1 behind");
    }

    private static readonly RepoActivity OnGitHub = new(new GitHubRepo("acme", "app"), "me", 0, 0, [], [], "main", new GitHubHead("new", 10));

    private static GitLine Compared(string? against, string branch = "main", string? head = "here") =>
        new(null, branch, 0, @"C:\app", IsRepository: true, GitHub: OnGitHub, Head: head, BehindAgainst: against);

    [Fact]
    public void A_line_compared_with_an_older_commit_than_GitHub_has_now_is_out_of_date()
    {
        // A git round can show the new read of GitHub beside an answer worked out against the old one (review of #426).
        Compared("old").BehindIsOutOfDate.ShouldBeTrue();
        Compared(null).BehindIsOutOfDate.ShouldBeTrue("never compared, though it can be");
        Compared("new").BehindIsOutOfDate.ShouldBeFalse();
    }

    [Fact]
    public void A_line_that_is_not_compared_is_not_out_of_date()
    {
        Compared(null, branch: "feature/x").BehindIsOutOfDate.ShouldBeFalse("only the default branch is compared");
        Compared(null, head: null).BehindIsOutOfDate.ShouldBeFalse("git could not tell its commit");
        (Compared(null) with { GitHub = null }).BehindIsOutOfDate.ShouldBeFalse();
        Compared("old", branch: "feature/x").BehindIsOutOfDate.ShouldBeTrue("it left the default branch: the old answer must go");
    }

    [Fact]
    public void An_answer_git_did_not_give_keeps_what_was_known_of_the_same_two_commits()
    {
        // "12 behind" must not turn "clean" because git ran out of time once (review of #426).
        var before = Compared("new") with { Behind = new GitBehind(12) };

        var line = Compared(null).WithBehind(BehindAnswer.Unknown, before, "new");

        line.Behind.ShouldBe(new GitBehind(12));
        line.BehindAgainst.ShouldBe("new");
    }

    [Fact]
    public void An_answer_git_did_not_give_for_other_commits_leaves_the_line_not_compared()
    {
        Compared(null).WithBehind(BehindAnswer.Unknown, Compared("old"), "new").ShouldBe(Compared(null), "it was not behind the old commit: nothing is known of the new one");
        Compared(null, head: "moved").WithBehind(BehindAnswer.Unknown, Compared("new") with { Behind = new GitBehind(12) }, "new").ShouldBe(Compared(null, head: "moved"), "the checkout moved: it may have pulled");
        Compared(null).WithBehind(BehindAnswer.Unknown, null, "new").ShouldBe(Compared(null));
    }

    [Fact]
    public void A_checkout_that_was_behind_and_has_not_moved_is_still_behind_when_GitHub_moved_on_and_git_does_not_answer()
    {
        // "12 behind" must not turn "clean", and Build new version open up, because a commit landed on GitHub while git was slow (review of #426).
        var before = Compared("old") with { Behind = new GitBehind(12) };

        var line = Compared(null).WithBehind(BehindAnswer.Unknown, before, "new");

        line.Behind.ShouldBe(new GitBehind(null), "how far, nobody knows now");
        line.BehindAgainst.ShouldBeNull("asked again next round");
        line.BehindLabel.ShouldBe("behind");
    }

    [Fact]
    public void An_answer_git_gave_is_taken_whatever_was_known()
    {
        var before = Compared("new") with { Behind = new GitBehind(12) };

        Compared(null).WithBehind(BehindAnswer.NotBehind, before, "new").ShouldBe(Compared("new"));
        Compared(null).WithBehind(BehindAnswer.Of(new GitBehind(3)), before, "new").Behind.ShouldBe(new GitBehind(3));
    }

    [Fact]
    public void The_same_two_commits_are_answered_by_the_line_before()
    {
        Compared(null).IsAnsweredBy(Compared("new") with { Behind = new GitBehind(12) }, "new").ShouldBeTrue();
        Compared(null).IsAnsweredBy(Compared("new"), "new").ShouldBeTrue("not behind is an answer too");
        Compared(null).IsAnsweredBy(Compared("old"), "new").ShouldBeFalse("GitHub's commit moved");
        Compared(null, head: "moved").IsAnsweredBy(Compared("new"), "new").ShouldBeFalse("the checkout's commit moved");
        Compared(null).IsAnsweredBy(Compared("new") with { Behind = new GitBehind(null) }, "new").ShouldBeTrue("behind without a count is an answer too");
        Compared(null).IsAnsweredBy(null, "new").ShouldBeFalse();
    }

    [Theory]
    [InlineData(1, "GitHub's main has 1 commit this checkout lacks. In the Yard, click the line and press Pull main.")]
    [InlineData(12, "GitHub's main has 12 commits this checkout lacks. In the Yard, click the line and press Pull main.")]
    [InlineData(null, "GitHub's main has commits this checkout lacks. In the Yard, click the line and press Pull main.")]
    public void The_behind_pill_says_what_to_do(int? commits, string tip)
    {
        Line(0, new GitBehind(commits)).BehindToolTip.ShouldBe(tip);
    }
}
