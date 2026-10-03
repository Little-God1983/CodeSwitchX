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
    }

    public ChatAsk Ask { get; }

    public IReadOnlyList<ChatQuestionView> Questions { get; }

    /// <summary>What the chat asks permission for; null for a question.</summary>
    public ChatPermission? Permission => Ask.Permission;

    /// <summary>"wants to run a command", or "'s Explore sub-agent wants to edit a file", after the chat's name.</summary>
    public string Wants => Permission is { } permission
        ? (permission.Agent is { } agent ? $"'s {agent} sub-agent wants to " : " wants to ") + permission.Wants
        : " asks";

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

    /// <summary>Every question has an option chosen, and the card is still open.</summary>
    public bool CanSend => IsOpen && Questions.All(q => q.HasAnswer);

    /// <summary>The answers as they go to the chat: one per question, several options joined by ", ".</summary>
    public IReadOnlyList<string> Answers() => Questions.Select(q => q.Answer).ToList();

    internal void ChoiceChanged() => OnPropertyChanged(nameof(CanSend));
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
