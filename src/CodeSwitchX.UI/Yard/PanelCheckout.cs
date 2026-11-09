namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The checkout of the repository line the GitHub panel was opened from (#407): where the panel's Pull acts, never a worktree.
/// </summary>
/// <param name="Path">The line's folder.</param>
/// <param name="Branch">The line's branch when the panel opened; null when git could not tell.</param>
/// <param name="ChatIsWorking">Whether a chat is working in the workspace now: the tile's state, read at the press. It errs on the careful side.</param>
/// <param name="Jobs">The Yard's jobs, which outlive the panel.</param>
public sealed record PanelCheckout(string Path, string? Branch, Func<bool> ChatIsWorking, YardGitJobs Jobs);
