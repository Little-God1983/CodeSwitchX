using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

/// <param name="DirtyCount">Changed and untracked files; null when git could not report them (not a repository, git missing, refused or timed out).</param>
/// <param name="GitDir">The repository's git directory, full path; two folders with the same one are in the same checkout.</param>
/// <param name="Head">The commit the checkout is at, the full hash (#426); null when git could not tell, or there is no commit yet.</param>
public sealed record GitInfo(bool IsRepository, string? Branch, int? DirtyCount, string? GitDir = null, string? Head = null);

/// <summary>A checkout's branch lacks commits its branch on GitHub has (#426).</summary>
/// <param name="Commits">How many; null when only "behind" can be told (a shallow clone, commits of its own, no count from GitHub).</param>
public sealed record GitBehind(int? Commits);

/// <param name="Output">Stdout when the process exited with code 0; otherwise null.</param>
/// <param name="TimedOut">The process was killed because it outlived the timeout.</param>
/// <param name="Pid">The started process id, when it started.</param>
/// <param name="ExitCode">The code it exited with; null when it never got that far (did not start, timed out, cancelled, a pipe broke).</param>
/// <param name="FailureOutput">Stdout when it exited with another code than 0; otherwise null.</param>
/// <param name="ErrorOutput">Stderr when it ran to its end; otherwise null.</param>
/// <param name="NotFound">The process did not start because there is no such program (Windows error 2 or 3). Another reason not to start, a program that is blocked say, is not this.</param>
public sealed record ProcessRunResult(string? Output, bool TimedOut, int? Pid, int? ExitCode = null, string? FailureOutput = null, string? ErrorOutput = null, bool NotFound = false)
{
    /// <summary>The process never started: no pid, no exit code, and not killed at a timeout.</summary>
    public bool DidNotStart => Pid is null && ExitCode is null && !TimedOut;
}

/// <summary>
/// Cheap git facts for tiles: branch from HEAD (no process), dirty count and commit from one <c>git status --porcelain=v2</c>, and how
/// far the checkout is behind a commit GitHub named (#426), without a fetch.
/// </summary>
public sealed class GitInspector
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The status line that carries the commit; "(initial)" stands there before the first one.</summary>
    private const string HeadLinePrefix = "# branch.oid ";

    /// <summary>Repositories using the reftable ref storage keep this placeholder in HEAD; only git can read the real ref.</summary>
    private const string ReftablePlaceholderBranch = ".invalid";

    private readonly Func<string, string, CancellationToken, Task<string?>> _runGit;
    private readonly string _profileDirectory;

    /// <param name="profileDirectory">
    /// The user profile (the default). A repository found at or above it while walking up from a workspace, such as a
    /// dotfiles repository, is not that workspace's repository.
    /// </param>
    public GitInspector(Func<string, string, CancellationToken, Task<string?>>? runGit = null, string? profileDirectory = null)
    {
        _runGit = runGit ?? RunGitAsync;
        _profileDirectory = profileDirectory ?? DefaultProfileDirectory;
    }

    private static string DefaultProfileDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// <see cref="GitDirOf"/> for the default user profile, for a caller with no inspector. <paramref name="dangling"/> is true when the
    /// folder has a <c>.git</c> file whose pointer leads nowhere: that is a repository that cannot be reached, not a folder without one.
    /// </summary>
    internal static string? GitDirOfFolder(string folder, out bool dangling) =>
        ResolveGitDir(folder, DefaultProfileDirectory, out dangling) is { } gitDir ? Path.GetFullPath(gitDir) : null;

    public static string? ReadBranch(string root) => ReadHead(ResolveGitDir(root, DefaultProfileDirectory));

    private static string? ReadHead(string? gitDir)
    {
        if (gitDir is null)
        {
            return null;
        }

        var headFile = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headFile))
        {
            return null;
        }

        var head = File.ReadAllText(headFile).Trim();
        const string refPrefix = "ref: refs/heads/";
        if (head.StartsWith(refPrefix, StringComparison.Ordinal))
        {
            return head[refPrefix.Length..];
        }

        return head.Length >= 7 ? head[..7] : head;
    }

    public async Task<GitInfo> InspectAsync(string root, CancellationToken ct)
    {
        // Off the caller's thread before touching the disk: callers start on the UI thread, and a folder on an offline
        // network share blocks Directory.Exists for about 20 s. Task.Yield would come back to the UI thread.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var gitDir = ResolveGitDir(root, _profileDirectory);
        if (gitDir is null)
        {
            return new GitInfo(false, null, null);
        }

        var branch = ReadHead(gitDir);
        if (branch == ReftablePlaceholderBranch)
        {
            branch = await ReadBranchFromGitAsync(root, ct).ConfigureAwait(false);
        }

        // Format 2 with the branch header: the commit comes with the files, at no process of its own (#426). Its "# ..." lines are no
        // files. Without the count against the upstream, which costs a walk nobody reads.
        var status = await _runGit(root, "status --porcelain=v2 --branch --no-ahead-behind --untracked-files=normal", ct).ConfigureAwait(false);
        var lines = status?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim('\r')).Where(l => l.Length > 0).ToList();
        int? dirty = lines?.Count(l => !l.StartsWith('#'));
        var head = lines?.FirstOrDefault(l => l.StartsWith(HeadLinePrefix, StringComparison.Ordinal))?[HeadLinePrefix.Length..].Trim();
        return new GitInfo(true, branch, dirty, Path.GetFullPath(gitDir), IsFullHash(head) ? head : null);
    }

    /// <summary>
    /// How far the checkout at <paramref name="head"/> is behind <paramref name="onGitHub"/>, the commit GitHub has for its branch (#426);
    /// null when it is not behind (it is at that commit, or holds it already: ahead, or GitHub was read before a pull) and when git did
    /// not answer: a made-up "behind" would send the user pulling for nothing. Nothing is fetched. A commit that is here is counted by
    /// git. One that is not is the usual case right after a merge on GitHub: then the branch is behind, and by the difference of the
    /// two totals when it has nothing of its own (its upstream, as last fetched, holds it whole) and the clone is not shallow.
    /// </summary>
    /// <param name="head">The checkout's commit, as <see cref="GitInfo.Head"/> has it.</param>
    /// <param name="commitsOnGitHub">How many commits GitHub's branch holds in all; null when it did not say.</param>
    public async Task<GitBehind?> BehindAsync(string root, string? head, string onGitHub, int? commitsOnGitHub, CancellationToken ct)
    {
        // What GitHub sent goes on git's command line only as a full hash.
        if (!IsFullHash(head) || !IsFullHash(onGitHub) || string.Equals(head, onGitHub, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (await _runGit(root, $"cat-file -e {onGitHub}^{{commit}}", ct).ConfigureAwait(false) is not null)
        {
            return Count(await _runGit(root, $"rev-list --count HEAD..{onGitHub}", ct).ConfigureAwait(false)) is > 0 and var missing ? new GitBehind(missing) : null;
        }

        // The commit is not here, or git does not answer at all: only a git that answers the next question says which.
        var shallow = (await _runGit(root, "rev-parse --is-shallow-repository", ct).ConfigureAwait(false))?.Trim();
        if (shallow is null)
        {
            return null;
        }

        if (shallow != "false" || commitsOnGitHub is not { } total
            || Count(await _runGit(root, "rev-list --count @{upstream}..HEAD", ct).ConfigureAwait(false)) != 0
            || Count(await _runGit(root, "rev-list --count HEAD", ct).ConfigureAwait(false)) is not { } here
            || total <= here)
        {
            return new GitBehind(null);
        }

        return new GitBehind(total - here);
    }

    /// <summary>What <c>git rev-list --count</c> printed, as a number; null when it printed none.</summary>
    internal static int? Count(string? output) =>
        int.TryParse(output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;

    /// <summary>A commit's full hash: 40 hex digits, or 64 in a SHA-256 repository.</summary>
    private static bool IsFullHash([NotNullWhen(true)] string? text) =>
        text is { Length: 40 or 64 } && text.All(char.IsAsciiHexDigit);

    /// <summary>
    /// The git directory of the repository <paramref name="folder"/> is in, full path as <see cref="GitInfo.GitDir"/> has
    /// it; null outside one. A file or two read, no process: cheap enough to find out whether a folder's checkout is one
    /// already inspected before running git status on it. Reads the disk; call it off the UI thread.
    /// </summary>
    public string? GitDirOf(string folder) => ResolveGitDir(folder, _profileDirectory) is { } gitDir ? Path.GetFullPath(gitDir) : null;

    private async Task<string?> ReadBranchFromGitAsync(string root, CancellationToken ct)
    {
        var current = (await _runGit(root, "branch --show-current", ct).ConfigureAwait(false))?.Trim();
        if (!string.IsNullOrEmpty(current))
        {
            return current;
        }

        var hash = (await _runGit(root, "rev-parse --short HEAD", ct).ConfigureAwait(false))?.Trim();
        return string.IsNullOrEmpty(hash) ? null : hash;
    }

    /// <summary>
    /// Whether git records a linked worktree for the repository (a folder under <c>.git\worktrees</c>): only then is a
    /// <c>git worktree list</c> worth a process. Reads the disk; call it off the UI thread.
    /// </summary>
    public bool HasLinkedWorktrees(string root)
    {
        var gitDir = ResolveGitDir(root, _profileDirectory);
        if (gitDir is null)
        {
            return false;
        }

        var worktrees = Path.Combine(gitDir, "worktrees");
        return Directory.Exists(worktrees) && Directory.EnumerateDirectories(worktrees).Any();
    }

    /// <summary>Runs git through the configured runner (a real process by default, a fake in tests).</summary>
    public Task<string?> RunAsync(string workingDirectory, string arguments, CancellationToken ct) => _runGit(workingDirectory, arguments, ct);

    public static async Task<string?> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        var result = await RunProcessAsync(GitStartInfo(workingDirectory, arguments), DefaultTimeout, ct).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>
    /// Git without optional locks: a plain <c>git status</c> refreshes the index under <c>.git/index.lock</c>, which collides
    /// with the user's own git commands and is left behind when the timeout kills the process. And without the network.
    /// </summary>
    internal static ProcessStartInfo GitStartInfo(string workingDirectory, string arguments)
    {
        var info = new ProcessStartInfo("git", arguments) { WorkingDirectory = workingDirectory };
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        // These are reads of what is on the disk. In a partial clone git would fetch an object it lacks from the remote, the commit
        // BehindAsync asks about for one (#426): never from here, and nobody could answer a prompt for a password.
        info.Environment["GIT_NO_LAZY_FETCH"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        return info;
    }

    /// <summary>
    /// Runs a process with stdout and stderr drained concurrently (a full stderr pipe would otherwise block the child),
    /// and kills the whole process tree when the timeout or the token fires so nothing leaks or keeps repo locks.
    /// </summary>
    internal static async Task<ProcessRunResult> RunProcessAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken ct)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;

        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null)
            {
                return new ProcessRunResult(null, false, null);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                // The pipes close when the last process holding them exits, which can be a grandchild that outlives the child
                // (cmd's "start /b"); the timeout covers the reads too, and the kill takes the whole tree with it.
                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                var succeeded = process.ExitCode == 0;
                return new ProcessRunResult(succeeded ? output : null, false, process.Id, process.ExitCode, succeeded ? null : output, error);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new ProcessRunResult(null, TimedOut: !ct.IsCancellationRequested, process.Id);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessRunResult(null, false, process?.Id, NotFound: ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 });
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // already gone
        }
    }

    private static string? ResolveGitDir(string root, string profileDirectory) => ResolveGitDir(root, profileDirectory, out _);

    private static string? ResolveGitDir(string root, string profileDirectory, out bool dangling)
    {
        dangling = false;
        if (!Directory.Exists(root))
        {
            return null;
        }

        // Walk up like git does: a workspace registered on a subfolder (a solution below the repository root) is still in
        // the repository. The walk stops before the user profile or any folder above it, where only a dotfiles
        // repository would be found.
        var start = new DirectoryInfo(Path.GetFullPath(root));
        var profile = string.IsNullOrWhiteSpace(profileDirectory) ? null : PathNormalizer.Normalize(profileDirectory);
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            if (dir != start && profile is not null && PathNormalizer.IsWithin(profile, PathNormalizer.Normalize(dir.FullName)))
            {
                return null;
            }

            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit))
            {
                return dotGit;
            }

            if (File.Exists(dotGit))
            {
                var followed = FollowGitDirPointer(dir.FullName, dotGit);
                dangling = followed is null;
                return followed;
            }
        }

        return null;
    }

    private static string? FollowGitDirPointer(string root, string dotGit)
    {
        var pointer = File.ReadAllText(dotGit).Trim();
        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = pointer[prefix.Length..].Trim();
        var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(root, target));
        // Without its trailing separator, so the one checkout has the one name and a quoted argument keeps its closing quote.
        return Directory.Exists(resolved) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved)) : null;
    }
}
