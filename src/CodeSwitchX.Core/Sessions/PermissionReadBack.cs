namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// What the app says to have the user confirm an allow Raven's brain proposed (#108): what the prompt allows, where, what
/// is risky in it, and that a yes allows it. The app says it, not the brain, so the yes answers this and nothing a brain
/// steered by a chat's words chose to ask.
/// </summary>
public static class PermissionReadBack
{
    /// <summary>How many characters of a command or path are said; the card shows all of it, and the yes allows all of it.</summary>
    public const int MaxSaid = 80;

    /// <summary>
    /// "Run npm test in CodeSwitchX? Say yes.", "Let its Explore sub-agent edit App.xaml.cs in CodeSwitchX? It writes
    /// outside its folder. Say yes." A long command is cut, and said to be.
    /// </summary>
    /// <param name="where">The workspace the chat runs in, as the user knows it; null when it is not known.</param>
    public static string Of(ChatAsk ask, string? where)
    {
        var permission = ask.Permission ?? throw new ArgumentException("A question has nothing to allow.", nameof(ask));
        var subject = TextCut.Cut(permission.Subject.ReplaceLineEndings(" ").Trim(), MaxSaid, "… (the rest is on the card)");

        var verb = permission.Wants switch
        {
            "search the web" => "search the web for",
            var wants when wants.StartsWith("use ", StringComparison.Ordinal) => wants + " with",
            var wants => wants.Split(' ')[0],
        };
        var who = permission.Agent is { } agent ? $"Let its {agent} sub-agent {verb}" : char.ToUpperInvariant(verb[0]) + verb[1..];
        var risks = permission.Risks is { Count: > 0 } found ? $" It {PermissionRisks.Phrase(found)}." : "";
        return $"{who} {subject}{(where is { Length: > 0 } ? $" in {where}" : "")}?{risks} Say yes.";
    }
}
