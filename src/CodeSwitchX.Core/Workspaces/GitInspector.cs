using System.Diagnostics;

namespace CodeSwitchX.Core.Workspaces;

public sealed record GitInfo(bool IsRepository, string? Branch, int DirtyCount);

/// <param name="Output">Stdout when the process exited with code 0; otherwise null.</param>
/// <param name="TimedOut">The process was killed because it outlived the timeout.</param>
/// <param name="Pid">The started process id, when it started.</param>
public sealed record ProcessRunResult(string? Output, bool TimedOut, int? Pid);

/// <summary>Cheap git facts for tiles: branch from HEAD (no process), dirty count from <c>git status --porcelain</c>.</summary>
public sealed class GitInspector
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, string, CancellationToken, Task<string?>> _runGit;

    public GitInspector(Func<string, string, CancellationToken, Task<string?>>? runGit = null)
    {
        _runGit = runGit ?? RunGitAsync;
    }

    public static string? ReadBranch(string root)
    {
        var gitDir = ResolveGitDir(root);
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
        if (ResolveGitDir(root) is null)
        {
            return new GitInfo(false, null, 0);
        }

        var branch = ReadBranch(root);
        var status = await _runGit(root, "status --porcelain --untracked-files=normal", ct).ConfigureAwait(false);
        var dirty = status is null
            ? 0
            : status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Trim('\r').Length > 0);
        return new GitInfo(true, branch, dirty);
    }

    /// <summary>Runs git through the configured runner (a real process by default, a fake in tests).</summary>
    public Task<string?> RunAsync(string workingDirectory, string arguments, CancellationToken ct) => _runGit(workingDirectory, arguments, ct);

    public static async Task<string?> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git", arguments) { WorkingDirectory = workingDirectory };
        var result = await RunProcessAsync(info, DefaultTimeout, ct).ConfigureAwait(false);
        return result.Output;
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

    private static string? ResolveGitDir(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

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
