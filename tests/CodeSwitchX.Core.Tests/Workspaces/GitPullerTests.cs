using System.Diagnostics;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

/// <summary>The pull of #407 against real git: an origin (bare), the user's checkout, and a second clone that pushes to the origin.</summary>
public class GitPullerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-pull-" + Guid.NewGuid().ToString("N"));
    private readonly GitPuller _puller = new();

    private string Origin => Path.Combine(_root, "origin.git");

    private string Work => Path.Combine(_root, "work");

    private string Other => Path.Combine(_root, "other");

    public void Dispose()
    {
        // git marks its object files read-only.
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<string> Git(string folder, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. arguments])
        {
            info.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("git is not installed");
            throw;
        }

        using var running = process ?? throw new InvalidOperationException("git did not start");
        var output = running.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = running.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await running.WaitForExitAsync(TestContext.Current.CancellationToken);
        running.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)}: {await error}");
        return (await output).Trim();
    }

    private static async Task Commit(string folder, string file, string text)
    {
        await File.WriteAllTextAsync(Path.Combine(folder, file), text, TestContext.Current.CancellationToken);
        await Git(folder, "add", file);
        await Git(folder, "commit", "-q", "-m", $"{file}: {text}");
    }

    /// <summary>An origin with one commit on main, the checkout on a feature branch with a commit of its own, and the other clone.</summary>
    private async Task Arrange()
    {
        Directory.CreateDirectory(_root);
        await Git(_root, "init", "-q", "--bare", "-b", "main", Origin);
        await Git(_root, "clone", "-q", Origin, Work);
        await Commit(Work, "a.txt", "one");
        await Git(Work, "push", "-q", "origin", "main");
        await Git(Work, "switch", "-q", "-c", "feature/x");
        await Commit(Work, "f.txt", "feature");
        await Git(_root, "clone", "-q", Origin, Other);
    }

    private async Task PushToMain(int commits)
    {
        for (var i = 0; i < commits; i++)
        {
            await Commit(Other, "b.txt", $"change {i}");
        }

        await Git(Other, "push", "-q", "origin", "main");
    }

    private Task<PullOutcome> Pull() => _puller.PullAsync(Work, "main", "work", TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_clean_feature_branch_ends_on_an_up_to_date_main()
    {
        await Arrange();
        await PushToMain(3);

        var outcome = await Pull();

        outcome.ShouldBe(new PullOutcome(true, "work is on main, 3 new commits."));
        (await Git(Work, "branch", "--show-current")).ShouldBe("main");
        (await Git(Work, "rev-parse", "HEAD")).ShouldBe(await Git(Other, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task On_main_already_it_pulls()
    {
        await Arrange();
        await Git(Work, "switch", "-q", "main");
        await PushToMain(1);

        (await Pull()).ShouldBe(new PullOutcome(true, "work is on main, 1 new commit."));

        (await Pull()).ShouldBe(new PullOutcome(true, "work is on main, no new commits."));
    }

    [Fact]
    public async Task Changes_to_tracked_files_refuse_and_leave_the_checkout_as_it_was()
    {
        await Arrange();
        await PushToMain(1);
        await File.WriteAllTextAsync(Path.Combine(Work, "f.txt"), "edited", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Work, "a.txt"), "edited", TestContext.Current.CancellationToken);

        (await Pull()).ShouldBe(new PullOutcome(false, "2 files have uncommitted changes."));

        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
    }

    [Fact]
    public async Task Untracked_files_do_not_refuse_and_go_along()
    {
        await Arrange();
        await File.WriteAllTextAsync(Path.Combine(Work, "notes.txt"), "mine", TestContext.Current.CancellationToken);

        (await Pull()).Succeeded.ShouldBeTrue();

        File.Exists(Path.Combine(Work, "notes.txt")).ShouldBeTrue();
        (await Git(Work, "branch", "--show-current")).ShouldBe("main");
    }

    [Fact]
    public async Task An_untracked_file_the_switch_would_overwrite_is_gits_refusal_and_nothing_switches()
    {
        await Arrange();
        await Commit(Other, "clash.txt", "theirs");
        await Git(Other, "push", "-q", "origin", "main");
        await File.WriteAllTextAsync(Path.Combine(Work, "clash.txt"), "mine", TestContext.Current.CancellationToken);
        var main = await Git(Work, "rev-parse", "main");

        var outcome = await Pull();

        outcome.Succeeded.ShouldBeFalse();
        outcome.Text.ShouldContain("clash.txt");
        outcome.Text.ShouldNotStartWith("error:");
        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
        (await File.ReadAllTextAsync(Path.Combine(Work, "clash.txt"), TestContext.Current.CancellationToken)).ShouldBe("mine");
        (await Git(Work, "rev-parse", "main")).ShouldBe(main, "the branch went back where it was");
    }

    [Fact]
    public async Task Local_commits_on_main_stop_the_pull_before_anything_switches()
    {
        await Arrange();
        await Git(Work, "switch", "-q", "main");
        await Commit(Work, "local.txt", "not pushed");
        await Git(Work, "switch", "-q", "feature/x");
        await PushToMain(1);

        (await Pull()).ShouldBe(new PullOutcome(false, "main has local commits that aren't on GitHub."));

        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
    }

    [Fact]
    public async Task Main_checked_out_in_another_worktree_refuses_and_leaves_that_worktree_alone()
    {
        await Arrange();
        var tree = Path.Combine(_root, "tree");
        await Git(Work, "worktree", "add", "-q", tree, "main");
        var before = await Git(tree, "rev-parse", "HEAD");
        await PushToMain(1);

        (await Pull()).ShouldBe(new PullOutcome(false, "main is checked out in another worktree."));

        (await Git(tree, "rev-parse", "HEAD")).ShouldBe(before, "the other worktree's branch did not move under its files");
        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
    }

    [Fact]
    public async Task After_a_merge_a_branch_with_commits_GitHub_has_not_got_is_not_left()
    {
        await Arrange();
        await Git(Work, "push", "-q", "origin", "feature/x");
        await Commit(Work, "late.txt", "not pushed");

        var outcome = await _puller.PullAsync(Work, "main", "work", TestContext.Current.CancellationToken, leavePushedOnly: true);

        outcome.ShouldBe(new PullOutcome(false, "feature/x has local commits that aren't on GitHub, so they are not in the merge."));
        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
    }

    [Fact]
    public async Task After_a_merge_a_pushed_branch_is_left_for_main()
    {
        await Arrange();
        await Git(Work, "push", "-q", "origin", "feature/x");

        (await _puller.PullAsync(Work, "main", "work", TestContext.Current.CancellationToken, leavePushedOnly: true)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_detached_head_refuses_so_its_commits_are_not_left_behind()
    {
        await Arrange();
        await Git(Work, "switch", "-q", "--detach");
        await Commit(Work, "loose.txt", "only here");

        (await Pull()).ShouldBe(new PullOutcome(false, "the checkout is not on a branch."));

        (await Git(Work, "log", "-1", "--format=%s")).ShouldBe("loose.txt: only here");
    }

    [Fact]
    public async Task The_last_check_can_stop_the_pull_after_the_fetch_and_nothing_switches()
    {
        await Arrange();

        var outcome = await _puller.PullAsync(Work, "main", "work", TestContext.Current.CancellationToken, _ => Task.FromResult<string?>("a chat started working here."));

        outcome.ShouldBe(new PullOutcome(false, "a chat started working here."));
        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
    }

    [Fact]
    public async Task An_edit_made_while_the_fetch_ran_refuses_the_switch()
    {
        await Arrange();

        var outcome = await _puller.PullAsync(Work, "main", "work", TestContext.Current.CancellationToken, async ct =>
        {
            await File.WriteAllTextAsync(Path.Combine(Work, "a.txt"), "edited meanwhile", ct);
            return null;
        });

        outcome.ShouldBe(new PullOutcome(false, "1 file has uncommitted changes."));
        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
    }

    [Fact]
    public async Task A_checkout_without_a_local_main_gets_one_from_GitHub()
    {
        await Arrange();
        await Git(Work, "branch", "-q", "-D", "main");
        await PushToMain(1);

        (await Pull()).ShouldBe(new PullOutcome(true, "work is on main."));

        (await Git(Work, "rev-parse", "--abbrev-ref", "main@{upstream}")).ShouldBe("origin/main");
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_says_so_and_nothing_switches()
    {
        await Arrange();
        await Git(Work, "remote", "set-url", "origin", Path.Combine(_root, "gone.git"));

        var outcome = await Pull();

        outcome.Succeeded.ShouldBeFalse();
        outcome.Text.ShouldNotBeNullOrWhiteSpace();
        (await Git(Work, "branch", "--show-current")).ShouldBe("feature/x");
    }

    [Theory]
    [InlineData("error: Your local changes would be overwritten.\nhint: commit them\nAborting\n", "Your local changes would be overwritten.")]
    [InlineData("fatal: 'origin' does not appear to be a git repository\nfatal: Could not read from remote repository.\n", "'origin' does not appear to be a git repository Could not read from remote repository.")]
    [InlineData("hint: only a hint\n", null)]
    [InlineData("", null)]
    public void Reason_is_gits_own_words_on_one_line(string printed, string? reason) => GitPuller.Reason(printed).ShouldBe(reason);
}
