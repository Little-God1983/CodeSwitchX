using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

public sealed record WorktreeInfo(string Path, string? Branch);

public sealed record WorkspaceProbeResult(
    string RootPath,
    string SuggestedName,
    string? WorkspaceFile,
    bool IsGitRepository,
    string? Branch,
    IReadOnlyList<string> SolutionFiles,
    bool HasClaudeMd,
    IReadOnlyList<WorktreeInfo> Worktrees);

/// <summary>Inspects what the user picked in "Add workspace" and proposes the registration.</summary>
public sealed class WorkspaceProbe
{
    private static readonly string[] SolutionPatterns = ["*.sln", "*.slnx"];
    private readonly GitInspector _git;

    public WorkspaceProbe(GitInspector git)
    {
        _git = git;
    }

    public async Task<WorkspaceProbeResult> ProbeAsync(string input, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        string root;
        string? workspaceFile = null;
        if (File.Exists(input))
        {
            var extension = Path.GetExtension(input);
            if (extension.Equals(".code-workspace", StringComparison.OrdinalIgnoreCase))
            {
                workspaceFile = Path.GetFullPath(input);
            }
            else if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Pick a folder, a .code-workspace file, or a .sln/.slnx solution.", nameof(input));
            }

            root = Path.GetDirectoryName(Path.GetFullPath(input))!;
        }
        else if (Directory.Exists(input))
        {
            root = Path.GetFullPath(input);
        }
        else
        {
            throw new DirectoryNotFoundException($"'{input}' does not exist.");
        }

        var canonicalRoot = PathNormalizer.Canonical(root);
        var git = await _git.InspectAsync(root, ct).ConfigureAwait(false);
        var worktrees = git.IsRepository
            ? ParseWorktreeList(await _git.RunAsync(root, "worktree list --porcelain", ct).ConfigureAwait(false) ?? string.Empty, canonicalRoot)
            : [];
        var solutions = SolutionPatterns.SelectMany(p => Directory.EnumerateFiles(root, p, SearchOption.TopDirectoryOnly)).OrderBy(f => f).ToList();

        return new WorkspaceProbeResult(
            canonicalRoot,
            Path.GetFileName(root.TrimEnd('\\', '/')),
            workspaceFile,
            git.IsRepository,
            git.Branch,
            solutions,
            File.Exists(Path.Combine(root, "CLAUDE.md")),
            worktrees);
    }

    /// <summary>
    /// Worktrees other than the main root, as canonical paths (real casing). Empty when the main root is not itself
    /// one of the worktrees, i.e. a subfolder of the repository: its worktrees are whole checkouts, not child roots.
    /// </summary>
    public static IReadOnlyList<WorktreeInfo> ParseWorktreeList(string porcelain, string mainRoot)
    {
        var mainKey = PathNormalizer.Normalize(mainRoot);
        var result = new List<WorktreeInfo>();
        var mainRootListed = false;
        string? path = null;
        string? branch = null;
        foreach (var raw in porcelain.Split('\n').Select(l => l.TrimEnd('\r')).Append(string.Empty))
        {
            if (raw.Length == 0)
            {
                if (path is not null)
                {
                    if (PathNormalizer.Normalize(path) == mainKey)
                    {
                        mainRootListed = true;
                    }
                    else
                    {
                        result.Add(new WorktreeInfo(PathNormalizer.Canonical(path), branch));
                    }
                }

                path = null;
                branch = null;
                continue;
            }

            if (raw.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = raw["worktree ".Length..];
            }
            else if (raw.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = raw["branch refs/heads/".Length..];
            }
        }

        return mainRootListed ? result : [];
    }
}
