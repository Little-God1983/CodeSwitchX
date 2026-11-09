using System.Globalization;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.GitHub;

/// <summary>What a merge came to.</summary>
/// <param name="Text">Why it was not merged, in short ("it has conflicts with main."); empty when it was.</param>
/// <param name="AlreadyMerged">GitHub says it was merged before: merged all the same, so the row leaves and a pull may follow (review of #408).</param>
public sealed record MergeOutcome(bool Merged, string Text = "", bool AlreadyMerged = false);

/// <summary>
/// Merges a pull request on GitHub with a merge commit, as the account that reads the repository, without switching gh's active one
/// (#408). The branch stays. Nothing local is touched: gh runs with <c>-R</c>, outside any checkout.
/// </summary>
public sealed class GitHubMerger
{
    private readonly IGhCli _gh;

    public GitHubMerger(IGhCli gh)
    {
        _gh = gh;
    }

    /// <param name="baseBranch">The branch it merges into, for the conflict's reason.</param>
    public async Task<MergeOutcome> MergeAsync(GitHubRepo repo, string account, int number, string baseBranch, CancellationToken ct)
    {
        // The long run's timeout: a merge killed midway may have merged or not, and nobody can tell from here.
        var answer = await _gh.ListAsAsync(account, ["pr", "merge", number.ToString(CultureInfo.InvariantCulture), "-R", repo.Slug, "--merge"], ct).ConfigureAwait(false);
        if (answer.Output is not null)
        {
            return new MergeOutcome(true);
        }

        if (answer.TimedOut)
        {
            return new MergeOutcome(false, "gh took too long. Check the pull request on GitHub.");
        }

        var printed = $"{answer.ErrorOutput}\n{answer.FailureOutput}";
        if (printed.Contains("was already merged", StringComparison.OrdinalIgnoreCase))
        {
            return new MergeOutcome(true, AlreadyMerged: true);
        }

        // Each on its own: an empty stderr must not hide the reason gh printed to stdout (review of #408).
        return new MergeOutcome(false, Reason(answer.ErrorOutput, baseBranch) ?? Reason(answer.FailureOutput, baseBranch) ?? "gh couldn't merge it. Is gh signed in?");
    }

    /// <summary>
    /// GitHub's reason in short: its "is not mergeable" reason, a conflict in the words of the panel, or gh's first line without the mark it
    /// starts a failure with. Shown in the panel only, never logged: it can name the repository.
    /// </summary>
    internal static string? Reason(string? printed, string baseBranch)
    {
        if (string.IsNullOrWhiteSpace(printed))
        {
            return null;
        }

        if (printed.Contains("cannot be cleanly created", StringComparison.OrdinalIgnoreCase))
        {
            return $"it has conflicts with {baseBranch}.";
        }

        var first = printed.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null)
        {
            return null;
        }

        const string NotMergeable = "is not mergeable: ";
        var at = first.IndexOf(NotMergeable, StringComparison.Ordinal);
        if (at >= 0)
        {
            var why = first[(at + NotMergeable.Length)..].Trim();
            return why.EndsWith('.') ? why : why + ".";
        }

        // gh starts a failure with a mark (X, or a cross in a terminal that shows one).
        if ((first.Length > 2 && first[1] == ' ' && !char.IsLetterOrDigit(first[0])) || first.StartsWith("X ", StringComparison.Ordinal))
        {
            first = first[2..];
        }

        return GitPuller.Reason(first);
    }
}
