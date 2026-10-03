using CodeSwitchX.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// What a chat asks, on the panel, open until it is answered here, left to VS Code, or the chat stops waiting for it.
/// A question (<see cref="RavenLogKind.Question"/>) has each question with its options to click: a card with one question
/// that takes one option is answered by the click; any other is sent once every question has an option
/// (<see cref="NeedsSend"/>). A permission prompt (<see cref="RavenLogKind.Permission"/>) has what the chat wants to do,
/// allowed or denied with a click.
/// </summary>
public sealed partial class ChatAskCard : ObservableObject
{
    public ChatAskCard(ChatAsk ask)
    {
        Ask = ask;
        Questions = ask.Questions.Select(q => new ChatQuestionView(this, q)).ToList();
        NeedsSend = Questions.Count > 1 || Questions.Any(q => q.MultiSelect);
        Suggestions = (ask.Suggestions ?? []).Select(s => new ChatSuggestionView(this, s)).ToList();
    }

    /// <summary>
    /// The standing rules Claude Code suggests with the prompt, one button each ("Always allow npm test in this folder,
    /// just you"); none when it suggests none. Chosen by a click only, never by voice.
    /// </summary>
    public IReadOnlyList<ChatSuggestionView> Suggestions { get; }

    public ChatAsk Ask { get; }

    public IReadOnlyList<ChatQuestionView> Questions { get; }

    /// <summary>What the chat asks permission for; null for a question.</summary>
    public ChatPermission? Permission => Ask.Permission;

    /// <summary>" wants to run a command", or "'s Explore sub-agent wants to edit a file", as said after the chat's name.</summary>
    public string Wants => Permission is null ? " asks" : WhoWants("'s ", " wants to ");

    /// <summary>The card's line under the chat's name: "Wants to run a command", "Its Explore sub-agent wants to edit a file".</summary>
    public string WantsLine => Permission is null ? "" : WhoWants("Its ", "Wants to ");

    /// <summary>
    /// What the permission is for, after who asks: "<paramref name="ofAgent"/>Explore sub-agent wants to …" for a
    /// sub-agent, "<paramref name="ofChat"/>…" for the chat's main agent.
    /// </summary>
    private string WhoWants(string ofAgent, string ofChat) =>
        (Permission!.Agent is { } agent ? $"{ofAgent}{agent} sub-agent wants to " : ofChat) + Permission.Wants;

    /// <summary>Who asks, as Raven says it: the chat (<see cref="Said"/>), or "…'s Explore sub-agent".</summary>
    public string Asker => Permission?.Agent is { } agent ? $"{Said}'s {agent} sub-agent" : Said;

    /// <summary>"Risky: deletes files and pushes to a remote", on the card; null when nothing in it is risky.</summary>
    public string? RiskLine => Permission?.Risks is { Count: > 0 } risks ? "Risky: " + PermissionRisks.Phrase(risks) : null;

    /// <summary>The card has a Send button: more than one question, or one that takes several options.</summary>
    public bool NeedsSend { get; }

    /// <summary>"ContentAutomatorX · Fix the upload retry", once the Yard has named the chat.</summary>
    [ObservableProperty]
    private string _chat = "A chat";

    /// <summary>The workspace whose tile shows the chat; null until named, or when it is on none.</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>The chat as Raven says it: "ContentAutomatorX, chat "Fix the upload retry"".</summary>
    public string Said { get; set; } = "A chat";

    /// <summary>The Yard naming the chat; read out only once it is done.</summary>
    public Task Naming { get; set; } = Task.CompletedTask;

    /// <summary>The question still waits for an answer here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _isOpen = true;

    /// <summary>How it ended ("Answered: Banana", "Left to VS Code"); null while it is open.</summary>
    [ObservableProperty]
    private string? _outcome;

    /// <summary>Raven's brain proposed to allow it on the user's word: the user's next yes, found by the app, allows it.</summary>
    [ObservableProperty]
    private bool _awaitsYes;

    /// <summary>The card's line while it awaits the yes.</summary>
    public static string AwaitsYesLine => "Say yes to allow it, or click.";

    /// <summary>Every question has an option chosen, and the card is still open.</summary>
    public bool CanSend => IsOpen && Questions.All(q => q.HasAnswer);

    /// <summary>The answers as they go to the chat: one per question, several options joined by ", ".</summary>
    public IReadOnlyList<string> Answers() => Questions.Select(q => q.Answer).ToList();

    internal void ChoiceChanged() => OnPropertyChanged(nameof(CanSend));
}

/// <summary>One "always allow" button of a permission card: a rule Claude Code suggested with the prompt.</summary>
public sealed class ChatSuggestionView
{
    /// <summary>How much of a long rule the button shows; its tooltip shows all of it.</summary>
    public const int MaxButtonChars = 60;

    internal ChatSuggestionView(ChatAskCard card, ChatPermissionSuggestion suggestion)
    {
        Card = card;
        Suggestion = suggestion;
    }

    public ChatAskCard Card { get; }

    public ChatPermissionSuggestion Suggestion { get; }

    /// <summary>The button: "Always allow npm test in this folder, just you"; a long rule is cut, where it is kept is not.</summary>
    public string Text
    {
        get
        {
            var label = Suggestion.Label.ReplaceLineEndings(" ");
            if (label.Length > MaxButtonChars)
            {
                var length = char.IsHighSurrogate(label[MaxButtonChars - 2]) ? MaxButtonChars - 2 : MaxButtonChars - 1; // never half an emoji
                label = label[..length].TrimEnd() + "…";
            }

            return Suggestion.Where.Length == 0 ? label : $"{label} {Suggestion.Where}";
        }
    }

    /// <summary>All of the rule, and what the click does.</summary>
    public string ToolTip => $"{Suggestion.Said}. The chat carries on, and Claude Code keeps the rule: it does not ask for this again.";
}

/// <summary>One question of a card, with its options.</summary>
public sealed class ChatQuestionView
{
    internal ChatQuestionView(ChatAskCard card, ChatQuestion question)
    {
        Card = card;
        Text = question.Text;
        Header = question.Header;
        MultiSelect = question.MultiSelect;
        Options = question.Options.Select(o => new ChatOptionView(this, o)).ToList();
    }

    public ChatAskCard Card { get; }

    public string Text { get; }

    public string? Header { get; }

    public bool MultiSelect { get; }

    public IReadOnlyList<ChatOptionView> Options { get; }

    public bool HasAnswer => Options.Any(o => o.IsChosen);

    public string Answer => string.Join(", ", Options.Where(o => o.IsChosen).Select(o => o.Label));

    /// <summary>Picks the option: alone where one is taken, toggled where several are.</summary>
    internal void Choose(ChatOptionView option)
    {
        if (MultiSelect)
        {
            option.IsChosen = !option.IsChosen;
        }
        else
        {
            foreach (var other in Options)
            {
                other.IsChosen = ReferenceEquals(other, option);
            }
        }

        Card.ChoiceChanged();
    }
}

/// <summary>One option of a question; chosen on the card until it is sent.</summary>
public sealed partial class ChatOptionView : ObservableObject
{
    internal ChatOptionView(ChatQuestionView question, ChatQuestionOption option)
    {
        Question = question;
        Label = option.Label;
        Description = option.Description;
    }

    public ChatQuestionView Question { get; }

    public string Label { get; }

    public string? Description { get; }

    [ObservableProperty]
    private bool _isChosen;
}
