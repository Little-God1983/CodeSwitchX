using System.Text.Json;
using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

public sealed record WorktreeInfo(string Path, string? Branch);

/// <summary>A local folder a <c>.code-workspace</c> file lists.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Name">The name the file gives the folder, which VS Code's Explorer shows; null when it gives none.</param>
public sealed record WorkspaceFolder(string Path, string? Name)
{
    /// <summary>What to call the folder: the file's name for it, else the folder's own.</summary>
    public string Label => Name ?? WorkspaceProbe.FolderName(Path);
}

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

        // Off the caller's thread before touching the disk: the Add workspace dialog probes from the UI thread, and an
        // offline network path blocks File.Exists for about 20 s. Task.Yield would come back to the UI thread.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        string root;
        string? name = null;
        string? workspaceFile = null;
        if (File.Exists(input))
        {
            if (IsWorkspaceFile(input))
            {
                workspaceFile = Path.GetFullPath(input);
                root = FirstFolderOf(workspaceFile);
                name = Path.GetFileNameWithoutExtension(workspaceFile);
            }
            else if (IsSolutionFile(input))
            {
                root = Path.GetDirectoryName(Path.GetFullPath(input))!;
            }
            else
            {
                throw new ArgumentException("Pick a folder, a .code-workspace file, or a .sln/.slnx solution.", nameof(input));
            }
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
            name ?? FolderName(root),
            workspaceFile,
            git.IsRepository,
            git.Branch,
            solutions,
            File.Exists(Path.Combine(root, "CLAUDE.md")),
            worktrees);
    }

    /// <summary>
    /// Whether <paramref name="path"/> has the shape of something <see cref="ProbeAsync"/> takes: a .code-workspace or
    /// .sln/.slnx file, or a path with no extension, which is taken for a folder. The Yard asks on the UI thread while a
    /// file is dragged over it, so the disk is never asked: an offline share in Explorer's Recent list would freeze both
    /// the Yard and Explorer's drag for about 20 s. Whether the path exists is left to the probe, which reports it in
    /// the dialog; a folder with a dot in its name is refused.
    /// </summary>
    public static bool LooksProbeable(string path) =>
        !string.IsNullOrWhiteSpace(path) && (IsWorkspaceFile(path) || IsSolutionFile(path) || Path.GetExtension(path).Length == 0);

    private static bool IsWorkspaceFile(string path) => Path.GetExtension(path).Equals(".code-workspace", StringComparison.OrdinalIgnoreCase);

    private static bool IsSolutionFile(string path) =>
        Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first local folder a <c>.code-workspace</c> file lists, which VS Code treats as the workspace's primary folder.
    /// The folder holding the file is not the root: a multi-root workspace often sits next to its repositories.
    /// </summary>
    private static string FirstFolderOf(string workspaceFile)
    {
        try
        {
            if (ListedFolders(workspaceFile).FirstOrDefault()?.Path is { } path)
            {
                return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"'{path}', the first folder of {workspaceFile}, does not exist.");
            }
        }
        catch (JsonException)
        {
        }

        throw new ArgumentException($"{workspaceFile} lists no local folder to use as the workspace root.", nameof(workspaceFile));
    }

    /// <summary>
    /// Every local folder a <c>.code-workspace</c> file lists, in its order, with full paths; empty when it lists none.
    /// Null when the file cannot be read or parsed right now: VS Code rewrites it on a settings change, so it can be locked
    /// or half-written for a moment, and that is no reason to drop the folders it listed. Read on every git round, so a
    /// folder added to the file shows without re-adding the tile. Reads the disk; call it off the UI thread.
    /// </summary>
    public static IReadOnlyList<WorkspaceFolder>? FoldersOf(string workspaceFile)
    {
        try
        {
            return ListedFolders(workspaceFile).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A folder's own name; the path itself for a drive or share root (<c>C:\</c>), which has none.</summary>
    public static string FolderName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    private static IEnumerable<WorkspaceFolder> ListedFolders(string workspaceFile)
    {
        var baseDirectory = Path.GetDirectoryName(workspaceFile)!;
        using var document = JsonDocument.Parse(File.ReadAllText(workspaceFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("folders", out var folders)
            || folders.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var folder in folders.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object))
        {
            var name = folder.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()
                : null;
            if (folder.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                yield return new WorkspaceFolder(Path.GetFullPath(Path.Combine(baseDirectory, p.GetString()!)), name);
            }
            else if (folder.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String
                && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && uri.IsFile)
            {
                yield return new WorkspaceFolder(uri.LocalPath, name);
            }
        }
    }

    /// <summary>
    /// The linked worktrees, as canonical paths (real casing), when <paramref name="mainRoot"/> is the repository's main
    /// worktree, which git lists first. Empty for any other root: a linked worktree or a subfolder of the repository
    /// must not claim the main checkout or sibling worktrees as child roots.
    /// </summary>
    public static IReadOnlyList<WorktreeInfo> ParseWorktreeList(string porcelain, string mainRoot)
    {
        var mainKey = PathNormalizer.Normalize(mainRoot);
        var result = new List<WorktreeInfo>();
        var entries = 0;
        string? path = null;
        string? branch = null;
        foreach (var raw in porcelain.Split('\n').Select(l => l.TrimEnd('\r')).Append(string.Empty))
        {
            if (raw.Length == 0)
            {
                if (path is not null)
                {
                    var isFirst = entries++ == 0;
                    if (isFirst && PathNormalizer.Normalize(path) != mainKey)
                    {
                        return [];
                    }

                    if (!isFirst)
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

        return result;
    }
}
