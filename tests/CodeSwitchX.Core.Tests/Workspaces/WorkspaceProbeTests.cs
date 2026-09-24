using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class WorkspaceProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-probe-" + Guid.NewGuid().ToString("N"), "MyApp");
    private readonly WorkspaceProbe _probe;

    public WorkspaceProbeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(_root, "MyApp.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Legacy.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# notes");
        File.WriteAllText(Path.Combine(_root, "MyApp.code-workspace"), "{}");
        var porcelain = $"worktree {_root}\nHEAD abc\nbranch refs/heads/main\n\nworktree {Path.Combine(_root, "..", "MyApp-wt")}\nHEAD def\nbranch refs/heads/feature\n\n";
        _probe = new WorkspaceProbe(new GitInspector((_, args, _) => Task.FromResult<string?>(args.Contains("worktree") ? porcelain : " M x\n")));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    [Fact]
    public async Task A_folder_is_probed_for_git_solutions_claude_md_and_worktrees()
    {
        var result = await _probe.ProbeAsync(_root, CancellationToken.None);

        result.RootPath.ShouldBe(PathNormalizer.Canonical(_root));
        result.SuggestedName.ShouldBe("MyApp");
        result.WorkspaceFile.ShouldBeNull();
        result.IsGitRepository.ShouldBeTrue();
        result.Branch.ShouldBe("main");
        result.SolutionFiles.Select(Path.GetFileName).ShouldBe(["Legacy.sln", "MyApp.slnx"], ignoreOrder: true);
        result.HasClaudeMd.ShouldBeTrue();
        var worktree = result.Worktrees.ShouldHaveSingleItem();
        worktree.Branch.ShouldBe("feature");
        worktree.Path.ShouldBe(PathNormalizer.Canonical(Path.Combine(_root, "..", "MyApp-wt")));
    }

    [Fact]
    public async Task A_solution_file_selects_its_folder_as_root()
    {
        var result = await _probe.ProbeAsync(Path.Combine(_root, "MyApp.slnx"), CancellationToken.None);

        result.RootPath.ShouldBe(PathNormalizer.Canonical(_root));
        result.SuggestedName.ShouldBe("MyApp");
        result.WorkspaceFile.ShouldBeNull();
    }

    [Fact]
    public async Task A_code_workspace_file_is_kept_as_the_launch_target()
    {
        var result = await _probe.ProbeAsync(Path.Combine(_root, "MyApp.code-workspace"), CancellationToken.None);

        result.RootPath.ShouldBe(PathNormalizer.Canonical(_root));
        result.WorkspaceFile.ShouldBe(Path.Combine(_root, "MyApp.code-workspace"));
    }

    [Fact]
    public async Task Unsupported_inputs_throw_a_clear_argument_error()
    {
        await Should.ThrowAsync<ArgumentException>(() => _probe.ProbeAsync(Path.Combine(_root, "CLAUDE.md"), CancellationToken.None));
        await Should.ThrowAsync<DirectoryNotFoundException>(() => _probe.ProbeAsync(Path.Combine(_root, "missing"), CancellationToken.None));
    }

    [Fact]
    public void ParseWorktreeList_skips_the_main_root_and_handles_detached_entries()
    {
        const string porcelain = "worktree C:/repo/app\nHEAD 111\nbranch refs/heads/main\n\nworktree C:/repo/app-a\nHEAD 222\nbranch refs/heads/a\n\nworktree C:/repo/app-b\nHEAD 333\ndetached\n\n";

        var list = WorkspaceProbe.ParseWorktreeList(porcelain, @"c:\repo\app");

        list.ShouldBe([new WorktreeInfo(@"C:\repo\app-a", "a"), new WorktreeInfo(@"C:\repo\app-b", null)], "worktree paths keep gits casing; only the main-root comparison is case-insensitive");
    }

    [Fact]
    public void ParseWorktreeList_finds_no_worktrees_for_a_subfolder_of_the_repository()
    {
        // From a subfolder, git lists the repository's top level as the main worktree; registering it as a
        // child root would claim every chat anywhere in the repository for this workspace.
        const string porcelain = "worktree C:/mono\nHEAD 111\nbranch refs/heads/main\n\nworktree C:/mono-wt\nHEAD 222\nbranch refs/heads/a\n\n";

        WorkspaceProbe.ParseWorktreeList(porcelain, @"c:\mono\services\api").ShouldBeEmpty();
    }
}
