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

    [Theory]
    [InlineData(1, "GitHub's main has 1 commit this checkout lacks. In the Yard, click the line and press Pull main.")]
    [InlineData(12, "GitHub's main has 12 commits this checkout lacks. In the Yard, click the line and press Pull main.")]
    [InlineData(null, "GitHub's main has commits this checkout lacks. In the Yard, click the line and press Pull main.")]
    public void The_behind_pill_says_what_to_do(int? commits, string tip)
    {
        Line(0, new GitBehind(commits)).BehindToolTip.ShouldBe(tip);
    }
}
