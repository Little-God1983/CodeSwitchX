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

    /// <summary>A commit in <see cref="RealGitRepos.Work"/> that changes the given files and has the given subject.</summary>
    private async Task CommitFiles(string subject, params string[] files)
    {
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(Path.Combine(Work, file), subject, TestContext.Current.CancellationToken);
            await Git(Work, "add", file);
        }

        await Git(Work, "commit", "-q", "-m", subject);
    }

    /// <summary>A stable build made from a commit in the checkout, followed by the build script's bump commit (#452): its short hash.</summary>
    private async Task<string> BuildAndBump()
    {
        await Arrange();
        await Commit(Work, "c.txt", "built");
        var builtFrom = await Git(Work, "rev-parse", "--short", "HEAD");
        await CommitFiles("chore: bump version to 1.0.0.2", "Directory.Build.props");
        return builtFrom;
    }

    [Fact]
    public async Task A_build_followed_only_by_its_bump_commit_is_up_to_date()
    {
        var builtFrom = await BuildAndBump();

        (await new GitInspector().SinceBuildAsync(Work, builtFrom, await Git(Work, "rev-parse", "HEAD"), TestContext.Current.CancellationToken))
            .ShouldBe(SinceBuildAnswer.UpToDate);
    }

    [Fact]
    public async Task Each_other_commit_on_the_branch_counts()
    {
        var builtFrom = await BuildAndBump();
        await Commit(Work, "a.txt", "two");
        // A bump's subject, but more than the version changed: real news.
        await CommitFiles("chore: bump version to 1.0.0.3", "Directory.Build.props", "b.txt");

        (await new GitInspector().SinceBuildAsync(Work, builtFrom, await Git(Work, "rev-parse", "HEAD"), TestContext.Current.CancellationToken))
            .ShouldBe(SinceBuildAnswer.Outdated(2));
    }

    [Fact]
    public async Task A_head_on_GitHub_the_clone_never_fetched_is_outdated_by_an_unknown_count()
    {
        // The everyday case right after a merge on GitHub.
        var builtFrom = await BuildAndBump();
        await PushToMain(1);

        (await new GitInspector().SinceBuildAsync(Work, builtFrom, await Git(Other, "rev-parse", "HEAD"), TestContext.Current.CancellationToken))
            .ShouldBe(SinceBuildAnswer.Outdated(null));
    }

    [Fact]
    public async Task The_build_commit_itself_is_up_to_date()
    {
        var builtFrom = await BuildAndBump();

        (await new GitInspector().SinceBuildAsync(Work, builtFrom, await Git(Work, "rev-parse", "HEAD~1"), TestContext.Current.CancellationToken))
            .ShouldBe(SinceBuildAnswer.UpToDate);
    }

    [Fact]
    public async Task A_line_on_a_subfolder_gets_the_paths_from_the_checkouts_top()
    {
        // Porcelain v2 writes paths relative to the folder git runs in (review of #444): a line on a subfolder must still get real paths.
        await Arrange();
        Directory.CreateDirectory(Path.Combine(Work, "sub"));
        await Commit(Work, "sub/b.txt", "one");
        File.WriteAllText(Path.Combine(Work, "a.txt"), "edited");
        File.WriteAllText(Path.Combine(Work, "sub", "b.txt"), "edited");

        var info = await new GitInspector().InspectAsync(Path.Combine(Work, "sub"), TestContext.Current.CancellationToken);

        info.ChangedFiles.ShouldNotBeNull().Files.Select(f => (f.Path, f.FullPath)).ShouldBe(
        [
            ("a.txt", Path.Combine(Work, "a.txt")),
            ("sub/b.txt", Path.Combine(Work, "sub", "b.txt")),
        ]);
    }

    [Fact]
    public async Task The_changed_files_come_with_their_real_names_and_kinds()
    {
        // An edit, a name git quotes (a space and an umlaut), and a staged rename (#444).
        await Arrange();
        await Commit(Work, "move.txt", "moved");
        File.WriteAllText(Path.Combine(Work, "a.txt"), "edited");
        File.WriteAllText(Path.Combine(Work, "ä b.txt"), "new");
        await Git(Work, "mv", "move.txt", "moved.txt");

        var info = await new GitInspector().InspectAsync(Work, TestContext.Current.CancellationToken);

        info.ChangedFiles.ShouldNotBeNull().Files.ShouldBe(
        [
            new ChangedFile("a.txt", Path.Combine(Work, "a.txt"), ChangeKind.Edited),
            new ChangedFile("moved.txt", Path.Combine(Work, "moved.txt"), ChangeKind.Renamed, "move.txt"),
            new ChangedFile("ä b.txt", Path.Combine(Work, "ä b.txt"), ChangeKind.New),
        ]);
    }
}
