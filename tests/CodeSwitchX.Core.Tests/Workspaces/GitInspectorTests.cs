using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class GitInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-git-" + Guid.NewGuid().ToString("N"));

    public GitInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Branch_is_read_from_a_symbolic_HEAD()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/feature/x\n");

        GitInspector.ReadBranch(_root).ShouldBe("feature/x");
    }

    [Fact]
    public void Detached_HEAD_gives_a_short_hash()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");

        GitInspector.ReadBranch(_root).ShouldBe("0123456");
    }

    [Fact]
    public void Worktrees_follow_the_gitdir_pointer_file()
    {
        var gitdir = Path.Combine(_root, "real-gitdir");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/wt-branch\n");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitdir}\n");

        GitInspector.ReadBranch(worktree).ShouldBe("wt-branch");
    }

    [Fact]
    public void Non_repositories_have_no_branch()
    {
        GitInspector.ReadBranch(_root).ShouldBeNull();
    }

    [Fact]
    public async Task Inspect_counts_porcelain_status_lines()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, args, _) => Task.FromResult<string?>(args.Contains("status") ? " M a.cs\n?? b.cs\n" : null));

        var info = await inspector.InspectAsync(_root, CancellationToken.None);

        info.ShouldBe(new GitInfo(true, "main", 2));
    }

    [Fact]
    public async Task Inspect_without_git_available_still_reports_the_branch()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(null));

        (await inspector.InspectAsync(_root, CancellationToken.None)).ShouldBe(new GitInfo(true, "main", 0));
        (await inspector.InspectAsync(Path.Combine(_root, "nope"), CancellationToken.None)).ShouldBe(new GitInfo(false, null, 0));
    }

    [Fact]
    public async Task A_hanging_process_is_killed_on_timeout_instead_of_leaking()
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { WorkingDirectory = _root };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await GitInspector.RunProcessAsync(info, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        result.TimedOut.ShouldBeTrue();
        result.Output.ShouldBeNull();
        var pid = result.Pid.ShouldNotBeNull();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Should.Throw<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid), "the whole process tree must be gone");
    }

    [Fact]
    public async Task Large_stderr_output_does_not_stall_the_run()
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"for /L %i in (1,1,3000) do @echo warning: line %i of a long complaint that fills the pipe 1>&2\"") { WorkingDirectory = _root };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await GitInspector.RunProcessAsync(info, TimeSpan.FromSeconds(20), CancellationToken.None);

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        result.TimedOut.ShouldBeFalse();
        result.Output.ShouldNotBeNull();
    }
}
