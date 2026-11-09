using System.Diagnostics;
using System.Globalization;

namespace CodeSwitchX.Core.Workspaces;

/// <summary>What a pull came to.</summary>
/// <param name="Succeeded">The checkout is on the default branch and up to date with GitHub.</param>
/// <param name="Text">On success the whole line ("CodeSwitchX is on main, 3 new commits."); otherwise only the reason, which the caller puts after its own words ("Pull stopped: ...").</param>
public sealed record PullOutcome(bool Succeeded, string Text);

/// <summary>
/// Switches a checkout to its default branch and brings it up to date with GitHub, fast-forward only (#407). Anything else is refused
/// with the reason and leaves the checkout as it was (on its branch, its files untouched): uncommitted changes to tracked files, a local default branch with commits that are
/// not on GitHub, and whatever git itself refuses (an untracked file the switch would overwrite). Never stashes, resets or merges.
/// </summary>
/// <param name="run">Runs git in a folder with the arguments and a timeout; a real process by default, a fake in tests.</param>
public sealed class GitPuller(Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessRunResult>>? run = null)
{
    /// <summary>For the reads: status, rev-parse, rev-list.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// For the fetch, the switch and the fast-forward: a large checkout takes a while, and killing a switch midway leaves a lock behind,
    /// so this is a safety net, not a limit anyone should meet.
    /// </summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The most of git's own words a reason carries; git can list many files.</summary>
    private const int MostReasonLength = 300;

    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessRunResult>> _run = run ?? RunGitAsync;

    /// <param name="checkout">The checkout's folder (a repository line's).</param>
    /// <param name="branch">The default branch, as GitHub names it.</param>
    /// <param name="name">What the success line calls the checkout: the repository's name.</param>
    public async Task<PullOutcome> PullAsync(string checkout, string branch, string name, CancellationToken ct)
    {
        // Tracked files only: untracked ones go along with the switch, and git refuses on its own when one would be overwritten.
        var status = await _run(checkout, ["status", "--porcelain", "--untracked-files=no"], ReadTimeout, ct).ConfigureAwait(false);
        if (status.Output is null)
        {
            return Failed(status, "git couldn't read the checkout.");
        }

        var changed = Lines(status.Output).Count;
        if (changed > 0)
        {
            return new PullOutcome(false, changed == 1 ? "1 file has uncommitted changes." : $"{changed} files have uncommitted changes.");
        }

        // An explicit refspec: the remote's own fetch setting may leave the default branch out (a single-branch clone).
        var remote = $"refs/remotes/origin/{branch}";
        var fetch = await _run(checkout, ["fetch", "--no-tags", "origin", $"+refs/heads/{branch}:{remote}"], WriteTimeout, ct).ConfigureAwait(false);
        if (fetch.Output is null)
        {
            return fetch.TimedOut ? new PullOutcome(false, "Fetching from GitHub took too long.") : Failed(fetch, "Couldn't fetch from GitHub.");
        }

        var local = $"refs/heads/{branch}";
        var before = (await _run(checkout, ["rev-parse", "--verify", "--quiet", local], ReadTimeout, ct).ConfigureAwait(false)).Output?.Trim();
        if (before is { Length: > 0 })
        {
            var ahead = await _run(checkout, ["rev-list", "--count", $"{remote}..{local}"], ReadTimeout, ct).ConfigureAwait(false);
            if (Count(ahead.Output) is not { } count)
            {
                return Failed(ahead, "git couldn't compare the branch with GitHub.");
            }

            if (count > 0)
            {
                return new PullOutcome(false, $"{branch} has local commits that aren't on GitHub.");
            }
        }

        var current = await _run(checkout, ["branch", "--show-current"], ReadTimeout, ct).ConfigureAwait(false);
        if (current.Output is null)
        {
            return Failed(current, "git couldn't read the checkout's branch.");
        }

        var onAnotherBranch = current.Output.Trim() != branch;
        if (onAnotherBranch)
        {
            // The branch is moved forward first, then checked out in one step: a switch git refuses (an untracked file it would overwrite)
            // leaves the checkout as it was. Moving a branch that has nothing of its own forward loses nothing; the old value guards a race.
            if (before is { Length: > 0 })
            {
                // update-ref does not look at worktrees: a worktree with the branch checked out would see its HEAD move under its files (review of #407).
                var trees = await _run(checkout, ["worktree", "list", "--porcelain"], ReadTimeout, ct).ConfigureAwait(false);
                if (trees.Output is null)
                {
                    return Failed(trees, "git couldn't list the worktrees.");
                }

                if (Lines(trees.Output).Contains($"branch {local}"))
                {
                    return new PullOutcome(false, $"{branch} is checked out in another worktree.");
                }

                var move = await _run(checkout, ["update-ref", "-m", "CodeSwitchX: fast-forward", local, remote, before], ReadTimeout, ct).ConfigureAwait(false);
                if (move.Output is null)
                {
                    return Failed(move, $"git couldn't bring {branch} up to date.");
                }
            }

            // A checkout that never had the branch locally gets it from GitHub's, tracking it.
            IReadOnlyList<string> arguments = before is { Length: > 0 } ? ["switch", "--quiet", branch] : ["switch", "--quiet", "--track", "-c", branch, $"origin/{branch}"];
            var change = await _run(checkout, arguments, WriteTimeout, ct).ConfigureAwait(false);
            if (change.Output is null)
            {
                return Failed(change, $"git couldn't switch to {branch}.");
            }
        }
        else
        {
            var forward = await _run(checkout, ["merge", "--ff-only", "--quiet", remote], WriteTimeout, ct).ConfigureAwait(false);
            if (forward.Output is null)
            {
                return Failed(forward, $"git couldn't bring {branch} up to date.");
            }
        }

        if (before is not { Length: > 0 })
        {
            return new PullOutcome(true, $"{name} is on {branch}.");
        }

        var added = Count((await _run(checkout, ["rev-list", "--count", $"{before}..HEAD"], ReadTimeout, ct).ConfigureAwait(false)).Output);
        var commits = added switch
        {
            null => string.Empty,
            0 => ", no new commits",
            1 => ", 1 new commit",
            var n => $", {n} new commits",
        };
        return new PullOutcome(true, $"{name} is on {branch}{commits}.");
    }

    /// <summary>What git said, in short, or <paramref name="otherwise"/> when it said nothing (it did not start, or ran out of time).</summary>
    private static PullOutcome Failed(ProcessRunResult run, string otherwise)
    {
        if (run.TimedOut)
        {
            return new PullOutcome(false, $"{otherwise.TrimEnd('.')}: git took too long.");
        }

        return new PullOutcome(false, Reason(run.ErrorOutput) ?? Reason(run.FailureOutput) ?? otherwise);
    }

    /// <summary>
    /// Git's own words as one line: hints and the closing "Aborting" left out, "error:" and "fatal:" taken off, the file list kept as it is
    /// (an untracked file the switch would overwrite is the reason). Shown in the panel only, never logged: it can carry file names.
    /// </summary>
    internal static string? Reason(string? printed)
    {
        if (string.IsNullOrWhiteSpace(printed))
        {
            return null;
        }

        var words = Lines(printed)
            .Where(l => !l.StartsWith("hint:", StringComparison.Ordinal) && l != "Aborting")
            .Select(l => l.StartsWith("error: ", StringComparison.Ordinal) ? l["error: ".Length..]
                : l.StartsWith("fatal: ", StringComparison.Ordinal) ? l["fatal: ".Length..]
                : l);
        var line = string.Join(" ", words).Trim();
        if (line.Length == 0)
        {
            return null;
        }

        return line.Length <= MostReasonLength ? line : line[..(MostReasonLength - 3)].TrimEnd() + "...";
    }

    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static int? Count(string? output) =>
        int.TryParse(output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;

    private static Task<ProcessRunResult> RunGitAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // As GitInspector's: a plain git status refreshes the index under .git/index.lock, which collides with the user's own git.
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        // Nobody can answer a prompt for a password or a host key: git fails at once instead of waiting for the timeout.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        return GitInspector.RunProcessAsync(info, timeout, ct);
    }
}
