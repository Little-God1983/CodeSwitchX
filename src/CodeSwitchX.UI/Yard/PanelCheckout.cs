namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The checkout of the repository line the GitHub panel was opened from (#407): where the panel's Pull acts, never a worktree.
/// </summary>
/// <param name="Path">The line's folder.</param>
/// <param name="Branch">The line's branch when the panel opened (the Yard keeps the panel's up to date); null when git could not tell.</param>
/// <param name="ChatIsWorking">Whether a chat is working, or waiting mid-turn, on any tile that shows the checkout, read at the press. By tile, not by chat folder: it errs on the careful side.</param>
/// <param name="Jobs">The Yard's jobs, which outlive the panel.</param>
/// <param name="GitDir">The checkout's git directory; null when git could not tell, and the folder stands in for it.</param>
public sealed record PanelCheckout(string Path, string? Branch, Func<bool> ChatIsWorking, YardGitJobs Jobs, string? GitDir = null)
{
    /// <summary>What the Yard's jobs know the checkout by (#412): its git directory, so two lines of one checkout share one job.</summary>
    public string Key { get; } = YardGitJobs.Key(GitDir ?? Path);
}
