using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// One repository's line on a tile: its branch and the git state pill. A workspace file that lists folders in several
/// repositories gets a line per repository, each named after its folder; a tile with one repository leaves the name out.
/// </summary>
/// <param name="Folder">The folder's name, as the workspace file gives it or its own; null on a tile with a single line.</param>
/// <param name="DirtyCount">Files with uncommitted changes; null when git could not tell.</param>
/// <param name="Path">The folder, full path: the tooltip shows it, so two folders with the same name tell apart. Null before the first git round.</param>
/// <param name="Issues">The repository's open issues; null when there are none or nothing is known yet.</param>
/// <param name="PullRequests">The repository's open pull requests; null when there are none or nothing is known yet.</param>
/// <param name="IsRepository">The folder is in a repository of its own, whether or not its branch could be read; false before the first git round and when there is no repository. A repository whose branch cannot be read shows "no git" and keeps its pills.</param>
/// <param name="GitDir">The checkout's git directory, full path (#412): two lines with the same one are one checkout, whatever their folders. Null when git could not tell.</param>
/// <param name="GitHub">What was last read on GitHub for the line's repository, whatever its counts: with nothing open there are no pills, but the line still opens the panel (#406). Null when the repository is not on GitHub or has not been read.</param>
/// <param name="StableBuild">The repository's stable-build scripts (#423), which the tile's menu builds and runs; null when it has none.</param>
/// <param name="Head">The commit the checkout is at, the full hash (#426); null when git could not tell.</param>
/// <param name="Behind">The line is on its repository's default branch and lacks commits GitHub has there (#426), as of the last read of GitHub; null when it is not behind, on another branch, or nothing is known.</param>
public sealed record GitLine(string? Folder, string? Branch, int? DirtyCount, string? Path = null, GitHubPill? Issues = null, GitHubPill? PullRequests = null, bool IsRepository = false, RepoActivity? GitHub = null, string? GitDir = null,
    StableBuildRepo? StableBuild = null, string? Head = null, GitBehind? Behind = null)
{
    /// <summary>Before the first git round: "no git" and no pill.</summary>
    public static readonly GitLine Unknown = new(null, null, null);

    public string BranchText => Branch ?? "no git";

    /// <summary>Between the folder's name and the branch; empty on a tile with a single line.</summary>
    public string Separator => Folder is null ? string.Empty : " · ";

    /// <summary>The whole line, untrimmed; what the tests read.</summary>
    public string Text => Folder + Separator + BranchText;

    /// <summary>The whole line, and the folder's path under it.</summary>
    public string ToolTipText => Path is null ? Text : $"{Text}\n{Path}";

    /// <summary>The state pill: "clean" or "3 changed". A line that is behind GitHub is not clean (#426), so it has none for no changed files.</summary>
    public string? GitStateLabel => Label(DirtyCount, Behind is not null);

    /// <summary>The second pill (#426): "12 behind", or "behind" where the count cannot be told; null when the line is not behind.</summary>
    public string? BehindLabel => Behind switch
    {
        null => null,
        { Commits: { } commits } => $"{commits} behind",
        _ => "behind",
    };

    /// <summary>The behind pill's tooltip: what it means and where the pull is. The same words in Raven's chat header, where the line is no button.</summary>
    public string? BehindToolTip => Behind is null ? null
        : $"GitHub's {BranchText} has {CommitsText(Behind.Commits)} this checkout lacks. In the Yard, click the line and press Pull {BranchText}.";

    /// <summary>Both pills in words, for Raven: "clean", "3 changed", "12 behind", "3 changed, 12 behind"; null when neither says anything.</summary>
    public string? GitStateText => (GitStateLabel, BehindLabel) switch
    {
        ({ } state, { } behind) => $"{state}, {behind}",
        var (state, behind) => state ?? behind,
    };

    private static string CommitsText(int? commits) => commits switch
    {
        null => "commits",
        1 => "1 commit",
        var n => $"{n} commits",
    };

    /// <summary>What a click on the line does, for a screen reader; null when the line is not on GitHub.</summary>
    public string? OpenGitHubText => GitHub is { } activity ? $"Open issues and pull requests of {activity.Repo.Name}" : null;

    /// <summary>
    /// "clean", or how many files have uncommitted changes (edited, staged or new). Null when git could not tell, so no
    /// pill: a made-up "clean" would hide changes. And null for no changed files on a branch that is <paramref name="behind"/>
    /// GitHub (#426): "clean" stands only where nothing is left to do.
    /// </summary>
    public static string? Label(int? dirtyCount, bool behind = false) => dirtyCount switch
    {
        null => null,
        0 => behind ? null : "clean",
        var n => $"{n} changed",
    };
}
