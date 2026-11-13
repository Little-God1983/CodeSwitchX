using System.Diagnostics.CodeAnalysis;
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
/// <param name="BehindAgainst">The commit of GitHub's default branch that <paramref name="Behind"/> was worked out against; null when the line was not compared. One that differs from what <paramref name="GitHub"/> has now says <paramref name="Behind"/> is out of date.</param>
/// <param name="Behind">The line is on its repository's default branch and lacks commits GitHub has there (#426), as of the last read of GitHub; null when it is not behind, on another branch, or nothing is known.</param>
public sealed record GitLine(string? Folder, string? Branch, int? DirtyCount, string? Path = null, GitHubPill? Issues = null, GitHubPill? PullRequests = null, bool IsRepository = false, RepoActivity? GitHub = null, string? GitDir = null,
    StableBuildRepo? StableBuild = null, string? Head = null, GitBehind? Behind = null, string? BehindAgainst = null)
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

    /// <summary>
    /// The commit this line is compared with (#426): where GitHub's default branch stands, for a line that is on that branch and knows
    /// its own commit; null for any other line, which is never behind.
    /// </summary>
    internal static GitHubHead? ComparedHead(GitLine line, RepoActivity? activity) =>
        line is { IsRepository: true, Path: not null, Branch: { } branch, Head: not null } && activity is { DefaultHead: { } head } && activity.DefaultBranch == branch ? head : null;

    /// <summary>
    /// <see cref="Behind"/> was worked out against another commit than GitHub has for the line now, or not at all: git must be asked
    /// again. Told by the line itself, not by comparing two reads of GitHub: a git round can show the new read beside the old answer.
    /// </summary>
    internal bool BehindIsOutOfDate => ComparedHead(this, GitHub)?.Sha != BehindAgainst;

    /// <summary>
    /// What <paramref name="before"/>, this folder's line as last shown, holds answers for this line too: the same commit here, the same
    /// commit on GitHub. Git is then not asked again, round after round: the same two commits give the same answer, and counting a
    /// large history every 30 s would hold up the round.
    /// </summary>
    internal bool IsAnsweredBy([NotNullWhen(true)] GitLine? before, string onGitHub) => SameCommitsAs(before, onGitHub);

    /// <summary>
    /// The line with git's answer against <paramref name="onGitHub"/>. An answer git did not give keeps what <paramref name="before"/>
    /// knew of the same two commits ("12 behind" does not turn "clean" because git ran out of time once). Where GitHub's commit moved on
    /// meanwhile, a checkout that was behind and has not moved still is, by a count nobody knows. Otherwise, and in that case too, the
    /// line is left not compared, to be asked again next round.
    /// </summary>
    internal GitLine WithBehind(BehindAnswer answer, GitLine? before, string onGitHub) =>
        answer.Known ? this with { Behind = answer.Behind, BehindAgainst = onGitHub }
        : SameCommitsAs(before, onGitHub) ? this with { Behind = before.Behind, BehindAgainst = onGitHub }
        : before is { Behind: not null } && before.Head == Head ? this with { Behind = new GitBehind(null), BehindAgainst = null }
        : this with { Behind = null, BehindAgainst = null };

    private bool SameCommitsAs([NotNullWhen(true)] GitLine? before, string onGitHub) => before is not null && before.Head == Head && before.BehindAgainst == onGitHub;

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

    /// <summary>"1 commit", "12 commits", or "commits" where the count is not known.</summary>
    internal static string CommitsText(int? commits) => commits switch
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
