using System.Diagnostics;
using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

/// <param name="DirtyCount">Changed and untracked files; null when git could not report them (not a repository, git missing, refused or timed out).</param>
public sealed record GitInfo(bool IsRepository, string? Branch, int? DirtyCount);

/// <param name="Output">Stdout when the process exited with code 0; otherwise null.</param>
/// <param name="TimedOut">The process was killed because it outlived the timeout.</param>
/// <param name="Pid">The started process id, when it started.</param>
public sealed record ProcessRunResult(string? Output, bool TimedOut, int? Pid);

/// <summary>Cheap git facts for tiles: branch from HEAD (no process), dirty count from <c>git status --porcelain</c>.</summary>
public sealed class GitInspector
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

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

        var status = await _runGit(root, "status --porcelain --untracked-files=normal", ct).ConfigureAwait(false);
        int? dirty = status is null
            ? null
            : status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Trim('\r').Length > 0);
        return new GitInfo(true, branch, dirty);
    }

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

    /// <summary>Runs git through the configured runner (a real process by default, a fake in tests).</summary>
    public Task<string?> RunAsync(string workingDirectory, string arguments, CancellationToken ct) => _runGit(workingDirectory, arguments, ct);

    public static async Task<string?> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        var result = await RunProcessAsync(GitStartInfo(workingDirectory, arguments), DefaultTimeout, ct).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>
    /// Git without optional locks: a plain <c>git status</c> refreshes the index under <c>.git/index.lock</c>, which collides
    /// with the user's own git commands and is left behind when the timeout kills the process.
    /// </summary>
    internal static ProcessStartInfo GitStartInfo(string workingDirectory, string arguments)
    {
        var info = new ProcessStartInfo("git", arguments) { WorkingDirectory = workingDirectory };
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
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
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new ProcessRunResult(null, TimedOut: !ct.IsCancellationRequested, process.Id);
            }

            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode == 0 ? output : null, false, process.Id);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessRunResult(null, false, process?.Id);
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

    private static string? ResolveGitDir(string root, string profileDirectory)
    {
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
                return FollowGitDirPointer(dir.FullName, dotGit);
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
        return Directory.Exists(resolved) ? resolved : null;
    }
}
