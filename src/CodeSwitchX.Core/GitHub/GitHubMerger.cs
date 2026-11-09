using System.Globalization;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.GitHub;

/// <summary>What a merge came to.</summary>
/// <param name="Text">Why it was not merged, in short ("it has conflicts with main."); empty when it was.</param>
/// <param name="AlreadyMerged">GitHub says it was merged before: merged all the same, so the row leaves and a pull may follow (review of #408).</param>
/// <param name="Unknown">gh ran out of time: it may have merged or not.</param>
/// <param name="Queued">GitHub put it in the merge queue (#414): not merged yet, so nothing is pulled and the row stays.</param>
/// <param name="Moved">The branch moved since the row was read (#414): GitHub refused, and the list is read again before another try.</param>
public sealed record MergeOutcome(bool Merged, string Text = "", bool AlreadyMerged = false, bool Unknown = false, bool Queued = false, bool Moved = false);

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
    /// <param name="headSha">The head commit the panel read (#414): GitHub merges only that, and refuses once the branch has moved. Null merges whatever is there.</param>
    public async Task<MergeOutcome> MergeAsync(GitHubRepo repo, string account, int number, string baseBranch, CancellationToken ct, string? headSha = null)
    {
        IReadOnlyList<string> arguments = ["pr", "merge", number.ToString(CultureInfo.InvariantCulture), "-R", repo.Slug, "--merge"];
        if (headSha is { Length: > 0 })
        {
            arguments = [.. arguments, "--match-head-commit", headSha];
        }

        // The long run's timeout: a merge killed midway may have merged or not, and nobody can tell from here.
        var answer = await _gh.ListAsAsync(account, arguments, ct).ConfigureAwait(false);
        if (answer.Output is not null)
        {
            // gh exits 0 when it only queued it. Without a terminal it prints no "will be added to the merge queue"; what reaches here is its
            // warning "The merge strategy for main is set by the merge queue", or "is already queued to merge" (#414, review).
            var said = $"{answer.Output}\n{answer.ErrorOutput}";
            return said.Contains("merge queue", StringComparison.OrdinalIgnoreCase) || said.Contains("queued to merge", StringComparison.OrdinalIgnoreCase)
                ? new MergeOutcome(false, "it will be added to the merge queue.", Queued: true)
                : new MergeOutcome(true);
        }

        if (answer.TimedOut)
        {
            return new MergeOutcome(false, "gh took too long. Check the pull request on GitHub.", Unknown: true);
        }

        var printed = $"{answer.ErrorOutput}\n{answer.FailureOutput}";
        if (printed.Contains("was already merged", StringComparison.OrdinalIgnoreCase))
        {
            return new MergeOutcome(true, AlreadyMerged: true);
        }

        // GitHub's answer to --match-head-commit when the branch moved: "Head branch was modified. Review and try the merge again."
        if (printed.Contains("branch was modified", StringComparison.OrdinalIgnoreCase))
        {
            return new MergeOutcome(false, "it changed since the panel read it. Look at it, then merge again.", Moved: true);
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

        // gh's warning on a repository with a merge queue comes first and is no reason (review of #414).
        var first = printed.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.Contains("is set by the merge queue", StringComparison.OrdinalIgnoreCase));
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
