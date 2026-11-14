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
        var asked = new List<string>();
        var inspector = new GitInspector((_, args, _) =>
        {
            asked.Add(args);
            return Task.FromResult<string?>(args.Contains("status")
                ? $"# branch.oid {Here}\n# branch.head main\n1 .M N... 100644 100644 100644 92cd 92cd a.cs\n? b.cs\n"
                : null);
        });

        var info = await inspector.InspectAsync(_root, CancellationToken.None);

        info.ShouldBe(new GitInfo(true, "main", 2, Path.Combine(_root, ".git"), Here, _root, TrackedChanges: 1), "the git directory tells folders of one checkout apart from another repository");
        asked.ShouldBe(["status --porcelain=v2 --branch --no-ahead-behind --untracked-files=normal"], "the commit comes with the status, at no process of its own");
    }

    [Theory]
    [InlineData("# branch.oid (initial)\n# branch.head main\n? a.cs\n", 1)]
    [InlineData(" M a.cs\n", 1)]
    [InlineData("", 0)]
    public async Task A_status_without_a_commit_still_counts_the_files(string status, int dirty)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(status));

        var info = await inspector.InspectAsync(_root, CancellationToken.None);

        info.DirtyCount.ShouldBe(dirty);
        info.Head.ShouldBeNull();
    }

    private const string Here = "defa35d0aa11defa35d0aa11defa35d0aa11defa";

    private const string OnGitHub = "7d6783b6e4ca65062d1ab6cdfe82e5b5f29e0728";

    /// <summary>The question that tells a commit that is here from one that is not; git answers it either way.</summary>
    private const string Probe = "rev-list --no-walk --ignore-missing";

    /// <summary>Git scripted by the start of its arguments; anything else fails, as a missing object or a git that does not answer does.</summary>
    private static GitInspector Scripted(List<string> ran, params (string Start, string? Output)[] answers) =>
        new((_, args, _) =>
        {
            ran.Add(args);
            return Task.FromResult(answers.FirstOrDefault(a => args.StartsWith(a.Start, StringComparison.Ordinal)).Output);
        });

    [Fact]
    public async Task A_checkout_at_GitHubs_commit_is_not_behind_and_asks_git_nothing()
    {
        var ran = new List<string>();

        (await Scripted(ran).BehindAsync(_root, OnGitHub, OnGitHub.ToUpperInvariant(), 1155, CancellationToken.None)).ShouldBe(BehindAnswer.NotBehind);

        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_GitHubs_commit_fetched_git_counts_how_far_behind()
    {
        var ran = new List<string>();
        var git = Scripted(ran, (Probe, OnGitHub + "\n"), ("rev-list --count", "12\n"), ("rev-parse --is-shallow-repository", "false\n"));

        (await git.BehindAsync(_root, Here, OnGitHub, 1155, CancellationToken.None)).ShouldBe(BehindAnswer.Of(new GitBehind(12)));

        ran.ShouldBe([$"{Probe} {OnGitHub}", $"rev-list --count HEAD..{OnGitHub}", "rev-parse --is-shallow-repository"]);
    }

    [Theory]
    [InlineData("true\n")]
    [InlineData(null)]
    public async Task In_a_shallow_clone_a_fetched_commit_says_behind_without_a_number(string? shallow)
    {
        // Fetched with --depth 1, GitHub's commit has no parents here: git would count 1 where GitHub is 12 ahead (review of #426).
        var git = Scripted([], (Probe, OnGitHub + "\n"), ("rev-list --count", "1\n"), ("rev-parse --is-shallow-repository", shallow));

        (await git.BehindAsync(_root, Here, OnGitHub, 1155, CancellationToken.None)).ShouldBe(BehindAnswer.Of(new GitBehind(null)));
    }

    [Fact]
    public async Task A_checkout_that_holds_GitHubs_commit_already_is_not_behind()
    {
        // Ahead of GitHub, or GitHub was read before the pull: its commit is part of this branch.
        var git = Scripted([], (Probe, OnGitHub + "\n"), ("rev-list --count", "0\n"));

        (await git.BehindAsync(_root, Here, OnGitHub, 1155, CancellationToken.None)).ShouldBe(BehindAnswer.NotBehind);
    }

    [Fact]
    public async Task Without_GitHubs_commit_the_count_is_the_difference_of_the_two_totals()
    {
        // The usual case right after a merge on GitHub: nothing fetched it. The branch has nothing of its own, so GitHub's holds it whole.
        var ran = new List<string>();
        var git = Scripted(ran, (Probe, "\n"), ("rev-parse --is-shallow-repository", "false\n"), ("rev-list --count @{upstream}..HEAD", "0\n"), ("rev-list --count HEAD", "1143\n"));

        (await git.BehindAsync(_root, Here, OnGitHub, 1155, CancellationToken.None)).ShouldBe(BehindAnswer.Of(new GitBehind(12)));

        ran.ShouldBe([$"{Probe} {OnGitHub}", "rev-parse --is-shallow-repository", "rev-list --count @{upstream}..HEAD", "rev-list --count HEAD"]);
    }

    [Theory]
    [InlineData("true\n", "0\n", "1143\n", 1155, "a shallow clone holds fewer commits than its branch has")]
    [InlineData(null, "0\n", "1143\n", 1155, "git did not say whether the clone is shallow")]
    [InlineData("false\n", "2\n", "1143\n", 1155, "commits of its own are not in GitHub's total")]
    [InlineData("false\n", null, "1143\n", 1155, "no upstream to say the branch has nothing of its own")]
    [InlineData("false\n", "0\n", null, 1155, "git did not count")]
    [InlineData("false\n", "0\n", "1143\n", null, "GitHub did not count")]
    [InlineData("false\n", "0\n", "1155\n", 1155, "the totals say level, the commits differ: the history was rewritten")]
    [InlineData("false\n", "0\n", "1160\n", 1155, "more commits than GitHub has")]
    public async Task Where_the_count_cannot_be_told_it_is_behind_without_a_number(string? shallow, string? own, string? total, int? onGitHub, string why)
    {
        var git = Scripted([], (Probe, string.Empty), ("rev-parse --is-shallow-repository", shallow), ("rev-list --count @{upstream}..HEAD", own), ("rev-list --count HEAD", total));

        (await git.BehindAsync(_root, Here, OnGitHub, onGitHub, CancellationToken.None)).ShouldBe(BehindAnswer.Of(new GitBehind(null)), why);
    }

    [Fact]
    public async Task A_git_that_does_not_answer_says_nothing_about_being_behind()
    {
        // Every command fails, as when git runs out of time: neither "the commit is not here" nor "not behind" may be made of that.
        var ran = new List<string>();
        var git = Scripted(ran);

        (await git.BehindAsync(_root, Here, OnGitHub, 1155, CancellationToken.None)).ShouldBe(BehindAnswer.Unknown);

        ran.ShouldBe([$"{Probe} {OnGitHub}"], "nothing more is asked of a git that does not answer");
    }

    [Fact]
    public async Task A_count_git_could_not_make_says_nothing_about_being_behind()
    {
        var git = Scripted([], (Probe, OnGitHub + "\n"));

        (await git.BehindAsync(_root, Here, OnGitHub, 1155, CancellationToken.None)).ShouldBe(BehindAnswer.Unknown, "a count that ran out of time is not \"not behind\"");
    }

    [Theory]
    [InlineData(null, OnGitHub)]
    [InlineData(Here, "7d6783b")]
    [InlineData(Here, "7d6783b6e4ca65062d1ab6cdfe82e5b5f29e0728 --output=x")]
    [InlineData(Here, "")]
    public async Task Without_both_commits_in_full_nothing_is_asked(string? head, string onGitHub)
    {
        var ran = new List<string>();

        (await Scripted(ran).BehindAsync(_root, head, onGitHub, 1155, CancellationToken.None)).ShouldBe(BehindAnswer.Unknown);

        ran.ShouldBeEmpty("what GitHub sent goes on git's command line only as a full hash");
    }

    [Fact]
    public async Task Inspect_without_git_available_still_reports_the_branch_but_no_dirty_count()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(null));

        (await inspector.InspectAsync(_root, CancellationToken.None)).ShouldBe(new GitInfo(true, "main", null, Path.Combine(_root, ".git"), Top: _root), "a failed status must not claim a clean tree");
        (await inspector.InspectAsync(Path.Combine(_root, "nope"), CancellationToken.None)).ShouldBe(new GitInfo(false, null, null));
    }

    [Fact]
    public async Task Inspect_does_its_file_and_process_work_off_the_calling_thread()
    {
        // The Yard starts refreshes on the UI thread; a folder on an offline network share blocks Directory.Exists for
        // about 20 s, so nothing may run synchronously before the first await.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        using var onCallingThread = new ThreadLocal<bool>();
        bool? ranOnCallingThread = null;
        var inspector = new GitInspector((_, _, _) =>
        {
            ranOnCallingThread ??= onCallingThread.Value;
            return Task.FromResult<string?>(string.Empty);
        });

        onCallingThread.Value = true;
        var inspect = inspector.InspectAsync(_root, CancellationToken.None);
        onCallingThread.Value = false;
        await inspect;

        ranOnCallingThread.ShouldBe(false);
    }

    [Fact]
    public void Git_runs_without_optional_locks_so_a_killed_status_never_leaves_index_lock_behind()
    {
        GitInspector.GitStartInfo(_root, "status").Environment["GIT_OPTIONAL_LOCKS"].ShouldBe("0");
    }

    [Fact]
    public void Git_never_goes_to_the_network_or_asks_for_a_password()
    {
        // In a partial clone a question about a commit that is not here would fetch it from the remote (review of #426).
        var environment = GitInspector.GitStartInfo(_root, "rev-list --no-walk --ignore-missing 7d6783b").Environment;

        environment["GIT_NO_LAZY_FETCH"].ShouldBe("1");
        environment["GIT_TERMINAL_PROMPT"].ShouldBe("0");
        environment["GCM_INTERACTIVE"].ShouldBe("never");
    }

    [Fact]
    public void Branch_is_found_from_a_subfolder_of_the_repository()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var sub = Path.Combine(_root, "services", "api");
        Directory.CreateDirectory(sub);

        GitInspector.ReadBranch(sub).ShouldBe("main");
    }

    [Fact]
    public async Task Tracked_changes_leave_untracked_files_out()
    {
        // What a pull stops at (#429): it lets untracked files go along with the switch.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, args, _) => Task.FromResult<string?>(args.Contains("status")
            ? "# branch.oid (initial)\n1 .M N... 100644 100644 100644 92cd 92cd a.cs\n2 R. N... 100644 100644 100644 1 2 R100 b.cs\tc.cs\n? new.cs\n"
            : null));

        var info = await inspector.InspectAsync(_root, CancellationToken.None);

        info.DirtyCount.ShouldBe(3, "the pill counts what git status lists, as before");
        info.TrackedChanges.ShouldBe(2);
    }

    [Theory]
    [InlineData("# branch.oid abc\n1 .M N... 100644 100644 100644 92cd 92cd a.cs\n? new.cs\n", 1)]
    [InlineData("M  a.cs\nR  b.cs -> c.cs\n", 2)]
    [InlineData("", 0)]
    public void Tracked_changes_are_counted_alike_in_either_status_format(string status, int tracked)
    {
        // The merge strip counts from format 2, the pull from format 1 without untracked files (#433); GitPullerTests runs both on one checkout.
        GitInspector.TrackedChangesIn(status.Split('\n')).ShouldBe(tracked);
    }

    [Fact]
    public async Task Inspect_treats_a_subfolder_of_a_repository_as_a_repository()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var sub = Path.Combine(_root, "src");
        Directory.CreateDirectory(sub);
        var inspector = new GitInspector((_, args, _) => Task.FromResult<string?>(args.Contains("status") ? " M a.cs\n" : null));

        var info = await inspector.InspectAsync(sub, CancellationToken.None);

        info.IsRepository.ShouldBeTrue();
        info.Branch.ShouldBe("main");
        info.Top.ShouldBe(_root, "the top is where the .git is, which the stable-build scripts are looked for at (#425)");
    }

    [Fact]
    public async Task A_pointer_ending_in_a_separator_gives_the_same_git_directory_to_inspecting_and_to_GitDirOf()
    {
        var gitdir = Path.Combine(_root, "real-gitdir");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/wt-branch\n");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitdir}{Path.DirectorySeparatorChar}\n");
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(string.Empty));

        var info = await inspector.InspectAsync(worktree, CancellationToken.None);

        info.GitDir.ShouldBe(gitdir);
        inspector.GitDirOf(worktree).ShouldBe(info.GitDir);
        info.Top.ShouldBe(worktree, "a worktree's top is the folder with the .git file, not the git directory it points at");
    }

    [Fact]
    public async Task GitDirOf_names_the_checkout_a_folder_is_in_without_running_git()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        var sub = Path.Combine(repo, "src");
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(plain);
        var inspector = new GitInspector((_, _, _) => throw new InvalidOperationException("no process for this"));
        var inspected = await new GitInspector((_, _, _) => Task.FromResult<string?>(string.Empty)).InspectAsync(repo, CancellationToken.None);

        inspector.GitDirOf(sub).ShouldBe(inspected.GitDir, "a subfolder is in the same checkout");
        inspector.GitDirOf(plain).ShouldBeNull();
    }

    [Fact]
    public async Task A_repository_at_the_profile_folder_does_not_claim_the_folders_below_it()
    {
        // A dotfiles repository in the user profile would otherwise make every folder under the profile a repository,
        // showing its branch and running git status over the whole profile on every refresh.
        var profile = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(profile, ".git"));
        File.WriteAllText(Path.Combine(profile, ".git", "HEAD"), "ref: refs/heads/dotfiles\n");
        var app = Path.Combine(profile, "code", "app");
        Directory.CreateDirectory(app);
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(string.Empty), profileDirectory: profile);

        (await inspector.InspectAsync(app, CancellationToken.None)).ShouldBe(new GitInfo(false, null, null));
        (await inspector.InspectAsync(profile, CancellationToken.None)).Branch.ShouldBe("dotfiles", "the profile folder itself is still that repository");
    }

    [Theory]
    [InlineData("master\n", "master")]
    [InlineData("\n", "abc1234")]
    public async Task A_reftable_repository_asks_git_for_the_branch_instead_of_showing_the_placeholder(string showCurrent, string expected)
    {
        // Repositories using the reftable ref storage keep a placeholder HEAD; the real ref lives in .git/reftable.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/.invalid\n");
        var inspector = new GitInspector((_, args, _) => Task.FromResult<string?>(
            args.StartsWith("branch --show-current", StringComparison.Ordinal) ? showCurrent
            : args.StartsWith("rev-parse --short", StringComparison.Ordinal) ? "abc1234\n"
            : string.Empty));

        (await inspector.InspectAsync(_root, CancellationToken.None)).Branch.ShouldBe(expected);
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
    public async Task A_non_zero_exit_reports_its_code_and_what_it_printed_apart_from_the_output()
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c echo printed& exit 3") { WorkingDirectory = _root };

        var result = await GitInspector.RunProcessAsync(info, TimeSpan.FromSeconds(20), CancellationToken.None);

        result.Output.ShouldBeNull();
        result.ExitCode.ShouldBe(3);
        result.FailureOutput!.Trim().ShouldBe("printed");
    }

    [Fact]
    public async Task A_zero_exit_has_output_and_no_failure_output()
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c echo printed") { WorkingDirectory = _root };

        var result = await GitInspector.RunProcessAsync(info, TimeSpan.FromSeconds(20), CancellationToken.None);

        result.Output!.Trim().ShouldBe("printed");
        result.ExitCode.ShouldBe(0);
        result.FailureOutput.ShouldBeNull();
    }

    [Fact]
    public async Task A_killed_process_reports_no_exit_code()
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { WorkingDirectory = _root };

        var result = await GitInspector.RunProcessAsync(info, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        result.ExitCode.ShouldBeNull();
        result.FailureOutput.ShouldBeNull();
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

    [Fact]
    public async Task A_grandchild_that_keeps_the_output_open_after_the_process_exited_is_killed_on_timeout_instead_of_throwing()
    {
        // cmd exits at once; the cmd it started in the background inherits the output pipe and holds it while its ping
        // writes to a file for 30 s. That file is the grandchildren's trace: it cannot be deleted while either holds it.
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c start /b cmd /c \"ping -n 30 127.0.0.1 > grandchild.txt\"") { WorkingDirectory = _root };
        var trace = Path.Combine(_root, "grandchild.txt");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await GitInspector.RunProcessAsync(info, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        result.TimedOut.ShouldBeTrue();
        result.Output.ShouldBeNull();
        File.Exists(trace).ShouldBeTrue("the grandchildren did run");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Should.NotThrow(() => File.Delete(trace), "a grandchild still holding the file survived the kill of the tree");
    }
}
