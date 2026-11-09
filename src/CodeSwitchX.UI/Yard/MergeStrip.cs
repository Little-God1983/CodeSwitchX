namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The strip a row's Merge button opens under the row (#408): the question, one line about the checkout, and the choices. A merge cannot
/// be undone, so it always takes the button and then a choice here.
/// </summary>
/// <param name="Row">The pull request's row, under which the strip shows.</param>
/// <param name="Question">"Merge #405 into main?"</param>
/// <param name="CheckoutLine">What happens to the workspace's checkout.</param>
/// <param name="OffersPull">Merge and pull is offered (and comes first): the checkout is on the pull request's branch, it merges into the default branch, and no chat is working here.</param>
/// <param name="MergeText">"Merge only" beside Merge and pull, or where the switch is left out for a working chat; "Merge" otherwise.</param>
/// <param name="BaseBranch">What the pull request merges into.</param>
public sealed record MergeStrip(GitHubRow Row, string Question, string CheckoutLine, bool OffersPull, string MergeText, string BaseBranch);
