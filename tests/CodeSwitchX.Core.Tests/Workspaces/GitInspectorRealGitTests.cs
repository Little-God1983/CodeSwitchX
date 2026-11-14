using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

/// <summary><see cref="GitInspector.BehindAsync(string, string?, string, int?, string?, CancellationToken)"/> on a real checkout (#435).</summary>
public class GitInspectorRealGitTests : RealGitRepos
{
    [Fact]
    public async Task A_branch_rewritten_here_is_not_behind_and_a_commit_pushed_from_elsewhere_is()
    {
        // A branch created from GitHub's three commits, squashed here, holds those three as its
        // own, though only the newest was ever its tip. A commit pushed on top from the other clone is behind, and it alone.
        await Arrange();
        await Git(Other, "switch", "-q", "-c", "feature/y");
        await Commit(Other, "y.txt", "1");
        await Commit(Other, "y.txt", "2");
        await Commit(Other, "y.txt", "3");
        await Git(Other, "push", "-q", "origin", "feature/y");
        await Git(Work, "fetch", "-q", "origin");
        await Git(Work, "switch", "-q", "feature/y");
        await Git(Work, "reset", "-q", "--soft", "HEAD~3");
        await Git(Work, "commit", "-q", "-m", "squashed");
        var inspector = new GitInspector();
        var onGitHub = await Git(Work, "rev-parse", "origin/feature/y");

        (await inspector.BehindAsync(Work, await Git(Work, "rev-parse", "HEAD"), onGitHub, null, "feature/y", TestContext.Current.CancellationToken))
            .ShouldBe(BehindAnswer.NotBehind);

        await Commit(Other, "y.txt", "4");
        await Git(Other, "push", "-q", "origin", "feature/y");
        await Git(Work, "fetch", "-q", "origin");
        onGitHub = await Git(Work, "rev-parse", "origin/feature/y");

        (await inspector.BehindAsync(Work, await Git(Work, "rev-parse", "HEAD"), onGitHub, null, "feature/y", TestContext.Current.CancellationToken))
            .ShouldBe(BehindAnswer.Of(new GitBehind(1, WentApart: true)), "beside the squashed commit of its own");
    }
}
