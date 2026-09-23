using System.Diagnostics;

namespace CodeSwitchX.Core.Workspaces;

public sealed record GitInfo(bool IsRepository, string? Branch, int DirtyCount);

/// <summary>Cheap git facts for tiles: branch from HEAD (no process), dirty count from <c>git status --porcelain</c>.</summary>
public sealed class GitInspector
{
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
        try
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            return null;
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
