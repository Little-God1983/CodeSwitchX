using CodeSwitchX.Core.Messaging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>What a chat can ask the user that CodeSwitchX can answer.</summary>
public enum ChatAskKind
{
    /// <summary>Claude's <c>AskUserQuestion</c>: one to four questions, each with options or an answer of the user's own.</summary>
    Question,

    /// <summary>
    /// A permission prompt (<c>PermissionRequest</c>): a tool the chat wants to use, allowed or denied. VS Code shows its own
    /// prompt for it all the while, and an answer there counts first.
    /// </summary>
    Permission,
}

/// <summary>
/// What a chat asks permission for, as the card shows it: what it wants ("run a command"), what exactly (the command, the
/// file, the URL, or the tool's input), and the sub-agent that asks, if one does. Nothing is cut: an allow allows all of it.
/// </summary>
/// <param name="Agent">The sub-agent's type ("general-purpose"); null when the chat's main agent asks.</param>
/// <param name="Details">All of the tool's input where <paramref name="Subject"/> does not say it all (an edit's change, a file's content); null otherwise.</param>
/// <param name="Risks">What is risky in it (<see cref="PermissionRisks"/>); null or empty when nothing is.</param>
public sealed record ChatPermission(string ToolName, string Wants, string Subject, string? Agent, string? Details = null,
    IReadOnlyList<PermissionRisk>? Risks = null);

/// <summary>
/// A standing rule Claude Code suggests with a permission prompt ("always allow npm test in this folder"), offered on the
/// card, by click only: it outlives the one command. <see cref="Json"/> is the suggestion as Claude Code sent it; it goes
/// back unchanged with the allow, and Claude Code writes the rule itself.
/// </summary>
/// <param name="Label">What it does: "Always allow npm test", "Allow all edits".</param>
/// <param name="Where">Where the rule is kept, as said after the label: "in this folder, just you"; empty when not told.</param>
/// <param name="Effect">What the click does from now on, for its tooltip: "Claude Code keeps the rule and does not ask for this again."</param>
public sealed record ChatPermissionSuggestion(string Json, string Label, string Where, string Effect = "")
{
    /// <summary>"Always allow npm test in this folder, just you".</summary>
    public string Said => Where.Length == 0 ? Label : $"{Label} {Where}";
}

/// <summary>
/// The user's answer to a permission prompt: allowed, or denied with what the chat is told; allowed for good when
/// <paramref name="Always"/> is the suggestion the user clicked.
/// </summary>
public sealed record ChatPermit(bool Allow, string? Message, ChatPermissionSuggestion? Always = null);

/// <summary>One option of a question, as the chat wrote it.</summary>
public sealed record ChatQuestionOption(string Label, string? Description);

/// <summary>One question a chat asks; <see cref="MultiSelect"/> lets the user pick more than one option.</summary>
public sealed record ChatQuestion(string Text, string? Header, IReadOnlyList<ChatQuestionOption> Options, bool MultiSelect);

/// <summary>
/// What a chat asks, held by its hook while the user answers in CodeSwitchX. <see cref="Step"/> is the hook event that
/// asks: a question's <c>PreToolUse</c>, a permission prompt's <c>PermissionRequest</c>; the chat and its agent. A question
/// has its <see cref="Questions"/>; a permission prompt has its <see cref="Permission"/>, which makes it one, and no
/// questions.
/// </summary>
/// <param name="Suggestions">The standing rules Claude Code suggests with a permission prompt; null when it suggests none.</param>
public sealed record ChatAsk(string Id, HookEvent Step, IReadOnlyList<ChatQuestion> Questions, ChatPermission? Permission = null,
    IReadOnlyList<ChatPermissionSuggestion>? Suggestions = null)
{
    /// <summary>What the brain is shown of a long command or input; the card shows all of it.</summary>
    public const int MaxDescribedChars = 300;

    public ChatAskKind Kind => Permission is null ? ChatAskKind.Question : ChatAskKind.Permission;

    public string SessionId => Step.SessionId;

    public DateTimeOffset At => Step.At;

    /// <summary>
    /// What it asks as Raven's brain reads it: the questions, "\"Which fruit?\" (one of: Apple, Banana, Cherry)" joined by
    /// "; ", or the permission, "permission to run a command: rm -rf dist; it deletes files".
    /// </summary>
    public string Describe() => Permission is { } permission
        ? $"permission to {permission.Wants}: {Shortened(permission.Subject)}" + (permission.Agent is { } agent ? $" (its {agent} sub-agent asks)" : "")
            + (permission.Risks is { Count: > 0 } risks ? $"; it {PermissionRisks.Phrase(risks)}" : "")
        : string.Join("; ", Questions.Select(q => $"\"{q.Text}\""
            + (q.Options.Count > 0 ? $" ({(q.MultiSelect ? "any of" : "one of")}: {string.Join(", ", q.Options.Select(o => o.Label))})" : "")));

    private static string Shortened(string text) => TextCut.Cut(text.ReplaceLineEndings(" "), MaxDescribedChars, "… (the card shows all of it)");
}

/// <summary>How a held ask ended.</summary>
public enum ChatAskOutcome
{
    /// <summary>The user answered it here; the chat carries on with the answers.</summary>
    Answered,

    /// <summary>The user left it to VS Code, which asks it in the chat's tab.</summary>
    ToVsCode,

    /// <summary>Nobody answered within <see cref="ChatAsks.Lifetime"/>: VS Code asks it in the chat's tab.</summary>
    TimedOut,

    /// <summary>The chat stopped waiting for it: its turn ended or was stopped, or Claude Code let the hook go.</summary>
    Gone,

    /// <summary>The user asked Raven to stop the chat (<see cref="ChatAsks.Stop"/>): the stop goes back in the ask's answer.</summary>
    Stopped,

    /// <summary>A permission prompt was answered in the chat's VS Code tab: the agent that asked has moved on.</summary>
    AnsweredInVsCode,
}

/// <summary>
/// A held ask that ended. Answered here, it has the answers given (one per question) for a question, or the
/// <see cref="Permit"/> for a permission prompt.
/// </summary>
public sealed record ChatAskClosed(ChatAsk Ask, ChatAskOutcome Outcome, IReadOnlyList<string>? Answers, ChatPermit? Permit = null);

/// <summary>
/// An allow Raven's brain proposed for a held permission prompt (<see cref="ChatAsks.Propose"/>). Nothing runs on it: the
/// prompt is allowed only when the app finds a yes in the user's next words (<see cref="ChatAsks.Confirm"/>).
/// </summary>
/// <param name="Window">The workspace of the Raven chat whose brain proposed it, which is told what came of it; null for chat 0, the Yard.</param>
/// <param name="FromRavenChat">False for a caller that is no Raven chat (#265): no brain of the user's is told what came of
/// it, and no turn of the user's answers there.</param>
public sealed record ChatAllowProposal(ChatAsk Ask, DateTimeOffset At, Guid? Window = null, bool FromRavenChat = true);

/// <summary>How a proposed allow ended.</summary>
public enum ChatProposalEnd
{
    /// <summary>The user said yes: the prompt is allowed.</summary>
    Confirmed,

    /// <summary>The user said something else: nothing ran, and the card stays open.</summary>
    Cancelled,

    /// <summary>The user said nothing within <see cref="ChatAsks.ProposalLifetime"/>: nothing ran, and the card stays open.</summary>
    Expired,

    /// <summary>The prompt ended meanwhile: answered on its card or in VS Code, or its turn ended.</summary>
    Closed,

    /// <summary>Another prompt's allow was proposed: only the newest proposal stands.</summary>
    Replaced,
}

/// <summary>
/// What chats ask the user, held while the user answers in CodeSwitchX. A chat's hook relay hands the ask over and waits
/// (<see cref="HoldAsync"/>): the answers given here (<see cref="Answer"/>) go back to Claude Code as the tool's input, and
/// an ask let go (<see cref="ToVsCode"/>, or after <see cref="Lifetime"/>) leaves the chat's tab to ask it as it always
/// does. Whether an ask is taken at all is <see cref="Takes"/>'s to say; one not taken goes to VS Code at once. While an
/// ask is held, the chat shows as waiting for the user, as it would with VS Code's own form open. A permission prompt
/// differs: VS Code shows its own prompt all the while, an answer there counts first, and Claude Code does not let the hook
/// go then; the end of the prompt's own tool use, or of the agent's turn, tells it (<see cref="Moves"/>). Thread-safe:
/// hooks, the bus and the window come on any thread.
/// </summary>
public sealed class ChatAsks : IDisposable
{
    /// <summary>An ask held this long goes to VS Code: the user is not answering it here. Below the hook's own timeout.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>What a chat denied here is told, unless the user said more.</summary>
    public const string DeniedMessage = "The user denied this in the Raven panel.";

    /// <summary>
    /// A proposed allow not confirmed within this of its read-back being heard lapses: nothing runs, and the card stays
    /// open for a click. The read-back's own length is not part of it.
    /// </summary>
    public static readonly TimeSpan ProposalLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A proposal whose read-back is not heard within this lapses too: the read-back is spoken, dropped or hushed well
    /// before, so this only keeps a proposal from standing for good.
    /// </summary>
    public static readonly TimeSpan ReadBackLifetime = TimeSpan.FromMinutes(2);

    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly IDisposable _steps;
    private readonly ITimer _sweep;
    private readonly ITimer _proposalExpiry;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);

    /// <summary>The allow proposed last and not ended; one at a time, the newest.</summary>
    private ChatAllowProposal? _proposed;

    /// <summary>The proposal that lapsed last, until another is made or its prompt ends: a yes said in time may come after.</summary>
    private ChatAllowProposal? _lapsed;

    /// <summary>
    /// When the user had heard (or been shown) the read-back of the standing proposal, and of the lapsed one: only words
    /// said after that answer it (<see cref="MarkHeard"/>). Null until then.
    /// </summary>
    private DateTimeOffset? _proposedHeard;
    private DateTimeOffset? _lapsedHeard;

    /// <summary>
    /// The tool uses begun lately, by their id (their PreToolUse), until they end: a permission prompt names no tool use, and
    /// is matched to its own by the tool and its input. The oldest go past <see cref="BegunKept"/>.
    /// </summary>
    private readonly Dictionary<string, HookEvent> _begun = new(StringComparer.Ordinal);
    private readonly Queue<string> _begunOrder = new();

    internal const int BegunKept = 512;

    /// <summary>
    /// The tool uses that ended lately with no prompt held for them: a prompt whose hook lands after its tool use ended (it
    /// was answered in VS Code before the relay got here) is no prompt any more.
    /// </summary>
    private readonly Queue<(HookEvent Begun, DateTimeOffset At)> _ended = new();

    internal const int EndedKept = 64;

    /// <summary>The asks taken here lately, held or ended, for <see cref="Explains"/>.</summary>
    private readonly Queue<(string SessionId, DateTimeOffset At)> _taken = new();

    internal const int TakenKept = 256;

    /// <summary>How often the held permission prompts are checked against what the chats' tabs show (<see cref="ShowsPrompt"/>).</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(2);

    /// <summary>A prompt this young is not checked yet: its tab may not show it yet.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    /// <summary>How near a chat's waiting must begin to an ask held here for the ask to account for it (<see cref="Explains"/>).</summary>
    public static readonly TimeSpan ExplainWindow = TimeSpan.FromSeconds(10);

    public ChatAsks(IEventBus bus, TimeProvider time)
    {
        _bus = bus;
        _time = time;
        _subscription = bus.Subscribe<SessionChanged>(Changed);
        _steps = bus.Subscribe<HookEventReceived>(received => Moves(received.Event));
        // Runs only while a permission prompt is held (Watch); it stops itself once none is.
        _sweep = time.CreateTimer(_ => Sweep(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _proposalExpiry = time.CreateTimer(_ => ExpireProposal(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether CodeSwitchX takes this ask, or leaves it to VS Code at once. Takes none until the app says otherwise.</summary>
    public Func<ChatAsk, bool> Takes { get; set; } = _ => false;

    /// <summary>
    /// Whether a held ask stays here when the window changes (<see cref="Recheck"/>), or goes to VS Code: the user may not
    /// see it here any more, or may be looking at the chat's own tab. Keeps every one until the app says otherwise.
    /// </summary>
    public Func<ChatAsk, bool> Keeps { get; set; } = _ => true;

    /// <summary>
    /// Whether the chat's VS Code tab shows a prompt now (Claude Code's own record of it waits on the user); null when that
    /// cannot be told. A permission prompt held here whose tab shows none was answered there, whatever its hooks said.
    /// Asked off the UI thread. Tells nothing until the app says otherwise.
    /// </summary>
    public Func<string, bool?> ShowsPrompt { get; set; } = _ => null;

    /// <summary>An ask is held now. Raised on the hook's thread.</summary>
    public event Action<ChatAsk>? Opened;

    /// <summary>A held ask ended, however it did. Raised on the thread that ended it.</summary>
    public event Action<ChatAskClosed>? Closed;

    /// <summary>An allow was proposed (<see cref="Propose"/>): the user's next words decide. Raised on the proposer's thread.</summary>
    public event Action<ChatAllowProposal>? ProposedAllow;

    /// <summary>A proposed allow ended, however it did. Raised on the thread that ended it.</summary>
    public event Action<ChatAllowProposal, ChatProposalEnd>? ProposalEnded;

    /// <summary>The allow proposed and still open for the user's yes; null when none is.</summary>
    public ChatAllowProposal? Proposed
    {
        get
        {
            lock (_lock)
            {
                return _proposed;
            }
        }
    }

    /// <summary>
    /// Holds the ask until it ends: how it ended, with the answers (one per question in their order) when it was answered
    /// here, or null when it was not taken. Any end but <see cref="ChatAskOutcome.Answered"/> or
    /// <see cref="ChatAskOutcome.Stopped"/> leaves VS Code to ask it. <paramref name="aborted"/> is the hook giving up (its
    /// turn stopped, Claude Code's timeout): the ask is gone.
    /// </summary>
    public async Task<ChatAskClosed?> HoldAsync(ChatAsk ask, CancellationToken aborted)
    {
        bool takes;
        try
        {
            takes = (ask.Permission is not null || ask.Questions.Count > 0) && Takes(ask);
        }
        catch (Exception)
        {
            takes = false; // a window that cannot say leaves the ask where it always was
        }

        if (!takes)
        {
            return null;
        }

        Held held;
        Held? replaced;
        lock (_lock)
        {
            if (ask.Kind == ChatAskKind.Permission && ask.Step.ToolUseId is null)
            {
                // The tool use it asks for, of those begun and not claimed by another prompt: its end tells when it was
                // answered in VS Code. One that ended already was answered before its hook got here.
                var claimed = _held.Values.Select(h => h.Ask.Step.ToolUseId).OfType<string>().ToHashSet(StringComparer.Ordinal);
                if (_begun.Values.Where(b => !claimed.Contains(b.ToolUseId!) && IsPromptOf(ask, b)).OrderBy(b => b.At).FirstOrDefault() is { } begun)
                {
                    ask = ask with { Step = ask.Step with { ToolUseId = begun.ToolUseId } };
                }
                else if (_ended.Any(e => IsPromptOf(ask, e.Begun) && e.At >= ask.At))
                {
                    return null;
                }
            }

            held = new Held(ask, new TaskCompletionSource<ChatAskClosed>(TaskCreationOptions.RunContinuationsAsynchronously));
            _held.Remove(ask.Id, out replaced);
            _held[ask.Id] = held;
            _taken.Enqueue((ask.SessionId, ask.At));
            while (_taken.Count > TakenKept)
            {
                _taken.Dequeue();
            }
            if (ask.Kind == ChatAskKind.Permission)
            {
                _sweep.Change(SweepEvery, SweepEvery);
            }
        }

        if (replaced is not null)
        {
            Finish(replaced, ChatAskOutcome.Gone, null);
        }

        // The window may have changed since Takes said yes, in a Recheck that could not see this ask yet.
        if (!KeepsSafely(ask))
        {
            Close(ask.Id, ChatAskOutcome.ToVsCode, null);
            return await held.Done.Task.ConfigureAwait(false);
        }

        // The chat waits for the user from now on, as it does with VS Code's own form open. A permission prompt's own
        // PermissionRequest tells that through the hooks for every event.
        if (ask.Kind == ChatAskKind.Question)
        {
            _bus.Publish(new HookEventReceived(Asking(ask)));
        }

        Opened?.Invoke(ask);
        try
        {
            return await held.Done.Task.WaitAsync(Lifetime, _time, aborted).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // An answer given just as the time ran out was told as given: it goes to the chat.
            Close(ask.Id, ChatAskOutcome.TimedOut, null);
            return await held.Done.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Claude Code let the hook go (the turn was stopped in its tab): nobody waits for the answer any more. After a
            // permission prompt the chat's own steps tell where it is.
            if (Close(ask.Id, ChatAskOutcome.Gone, null) && ask.Kind == ChatAskKind.Question)
            {
                _bus.Publish(new HookEventReceived(LetGo(ask, _time.GetUtcNow())));
            }

            return await held.Done.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Answers a held ask: one answer per question, in their order (an option's label, several joined by ", ", or the
    /// user's own words). False when it is not held any more.
    /// </summary>
    /// <exception cref="ArgumentException">Not one answer per question, or one is blank.</exception>
    public bool Answer(string askId, IReadOnlyList<string> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ChatAsk? ask;
        lock (_lock)
        {
            ask = _held.GetValueOrDefault(askId)?.Ask;
        }

        if (ask is null)
        {
            return false;
        }

        if (ask.Kind != ChatAskKind.Question)
        {
            throw new ArgumentException("The chat asks for permission, not a question: it is allowed or denied.", nameof(askId));
        }

        if (answers.Count != ask.Questions.Count)
        {
            throw new ArgumentException($"The chat asks {ask.Questions.Count} question{(ask.Questions.Count == 1 ? "" : "s")}; {answers.Count} answer{(answers.Count == 1 ? " was" : "s were")} given.", nameof(answers));
        }

        if (answers.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Every question needs an answer.", nameof(answers));
        }

        return Close(askId, ChatAskOutcome.Answered, answers.Select(a => a.Trim()).ToList());
    }

    /// <summary>
    /// Allows or denies a held permission prompt; a deny tells the chat <paramref name="message"/>, or
    /// <see cref="DeniedMessage"/>, and the chat carries on. False when it is not held any more.
    /// </summary>
    /// <param name="always">
    /// One of the prompt's own <see cref="ChatAsk.Suggestions"/>, to allow it for good: the user's click on its button, and
    /// nothing else (no voice, no brain tool) gives it.
    /// </param>
    /// <exception cref="ArgumentException">The ask is a question; or <paramref name="always"/> comes with a deny, or is no suggestion of this prompt.</exception>
    public bool Permit(string askId, bool allow, string? message = null, ChatPermissionSuggestion? always = null)
    {
        ChatAsk? ask;
        lock (_lock)
        {
            ask = _held.GetValueOrDefault(askId)?.Ask;
        }

        if (ask is null)
        {
            return false;
        }

        if (ask.Kind != ChatAskKind.Permission)
        {
            throw new ArgumentException("The chat asks a question, not for permission: it needs answers.", nameof(askId));
        }

        if (always is not null && (!allow || ask.Suggestions?.Contains(always) != true))
        {
            throw new ArgumentException("Only a rule Claude Code suggested with this prompt can be allowed for good, and only with an allow.", nameof(always));
        }

        var said = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        return Close(askId, ChatAskOutcome.Answered, null, new ChatPermit(allow, allow ? said : said ?? DeniedMessage, always));
    }

    /// <summary>
    /// Raven's brain proposes to allow a held permission prompt, on the user's word. Nothing runs: the prompt is allowed
    /// only by <see cref="Confirm"/>, when the app finds a yes in the user's next words, which no brain tool can give. Any
    /// other words (<see cref="Cancel"/>), none within <see cref="ProposalLifetime"/>, the prompt ending, or another
    /// proposal end it, and the card stays open for a click. The app, not the brain, reads the prompt back and asks for
    /// the yes (on <see cref="ProposedAllow"/>), so the yes answers what the app said.
    /// </summary>
    /// <param name="window">The workspace of the Raven chat whose brain proposes it; null for chat 0, the Yard.</param>
    /// <param name="fromRavenChat">False for a caller that is no Raven chat: see <see cref="ChatAllowProposal.FromRavenChat"/>.</param>
    /// <exception cref="ArgumentException">The ask is not held, or is a question.</exception>
    public ChatAllowProposal Propose(string askId, Guid? window = null, bool fromRavenChat = true)
    {
        ChatAllowProposal proposal;
        ChatAllowProposal? replaced;
        lock (_lock)
        {
            var ask = _held.GetValueOrDefault(askId)?.Ask
                ?? throw new ArgumentException("The chat no longer waits for that: it was answered, left to VS Code, or its turn ended.", nameof(askId));
            if (ask.Kind != ChatAskKind.Permission)
            {
                throw new ArgumentException("The chat asks a question, not for permission: it needs answers.", nameof(askId));
            }

            replaced = _proposed;
            _lapsed = null;
            (_proposedHeard, _lapsedHeard) = (null, null);
            proposal = _proposed = new ChatAllowProposal(ask, _time.GetUtcNow(), window, fromRavenChat);
            _proposalExpiry.Change(ReadBackLifetime, Timeout.InfiniteTimeSpan); // the user's 30 s start once it is heard
        }

        if (replaced is not null)
        {
            ProposalEnded?.Invoke(replaced, ChatProposalEnd.Replaced);
        }

        ProposedAllow?.Invoke(proposal);
        return proposal;
    }

    /// <summary>
    /// The user has heard the read-back of <paramref name="proposal"/> to its end, or been shown it where Raven does not
    /// speak, at <paramref name="at"/>: only words said after that answer it. A yes said before the app asked for it, or
    /// to a read-back cut off midway, allows nothing. The user has <see cref="ProposalLifetime"/> from then. False when it
    /// no longer stands.
    /// </summary>
    public bool MarkHeard(ChatAllowProposal proposal, DateTimeOffset at) => MarkHeard(proposal, at, out _);

    /// <inheritdoc cref="MarkHeard(ChatAllowProposal, DateTimeOffset)"/>
    /// <param name="first">Whether this call marked it: it was not heard before.</param>
    public bool MarkHeard(ChatAllowProposal proposal, DateTimeOffset at, out bool first)
    {
        lock (_lock)
        {
            first = false;
            if (!ReferenceEquals(_proposed, proposal))
            {
                return false;
            }

            if (_proposedHeard is null)
            {
                first = true;
                _proposedHeard = at;
                _proposalExpiry.Change(Remaining(at + ProposalLifetime), Timeout.InfiniteTimeSpan);
            }

            return true;
        }
    }

    /// <summary>Whether the user has heard the read-back of <paramref name="proposal"/> (<see cref="MarkHeard"/>).</summary>
    public bool IsHeard(ChatAllowProposal proposal)
    {
        lock (_lock)
        {
            return (ReferenceEquals(_proposed, proposal) && _proposedHeard is not null) || (ReferenceEquals(_lapsed, proposal) && _lapsedHeard is not null);
        }
    }

    /// <summary>
    /// The proposal words the user finished speaking at <paramref name="said"/> answer: the one standing, if they were said
    /// after its read-back was heard (<see cref="MarkHeard"/>); or the one that lapsed last, if they were said after that
    /// and within <see cref="ProposalLifetime"/> of it, and only transcribed after it lapsed (its prompt still held). Null when they
    /// answer none: words said before the read-back ended are about something else.
    /// </summary>
    public ChatAllowProposal? ProposalFor(DateTimeOffset said)
    {
        lock (_lock)
        {
            if (_proposed is { } standing)
            {
                return _proposedHeard is { } heard && said >= heard ? standing : null;
            }

            return _lapsed is { } lapsed && _lapsedHeard is { } lapsedHeard && said >= lapsedHeard && said - lapsedHeard <= ProposalLifetime
                && _held.ContainsKey(lapsed.Ask.Id) ? lapsed : null;
        }
    }

    /// <summary>
    /// The user said yes to <paramref name="proposal"/> (from <see cref="ProposalFor"/>): the prompt is allowed, and the chat
    /// carries on. False when it no longer stands, or the prompt ended meanwhile.
    /// </summary>
    public bool Confirm(ChatAllowProposal proposal)
    {
        if (!Take(proposal, out _))
        {
            return false;
        }

        // A lapsed one too: the yes was said in time, and transcribed after.
        var allowed = Permit(proposal.Ask.Id, allow: true);
        ProposalEnded?.Invoke(proposal, allowed ? ChatProposalEnd.Confirmed : ChatProposalEnd.Closed);
        return allowed;
    }

    /// <summary>
    /// The user said something else to <paramref name="proposal"/>: it is dropped, and nothing runs. True only when it was
    /// still standing, and then <see cref="ProposalEnded"/> tells it as <see cref="ChatProposalEnd.Cancelled"/>; a lapsed
    /// one is only forgotten, as its lapse was told already.
    /// </summary>
    public bool Cancel(ChatAllowProposal proposal)
    {
        if (!Take(proposal, out var standing) || !standing)
        {
            return false;
        }

        ProposalEnded?.Invoke(proposal, ChatProposalEnd.Cancelled);
        return true;
    }

    /// <summary>Takes <paramref name="proposal"/>, standing or lapsed, so nothing else ends it too; false when it is neither.</summary>
    private bool Take(ChatAllowProposal proposal, out bool standing)
    {
        lock (_lock)
        {
            standing = ReferenceEquals(_proposed, proposal);
            if (standing)
            {
                _proposed = null;
                _proposedHeard = null;
                _proposalExpiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return true;
            }

            if (ReferenceEquals(_lapsed, proposal))
            {
                _lapsed = null;
                _lapsedHeard = null;
                return true;
            }

            return false;
        }
    }

    /// <summary>The proposal for <paramref name="askId"/>, standing or lapsed, taken as its prompt ends; null when there is none.</summary>
    private ChatAllowProposal? TakeProposal(string askId)
    {
        lock (_lock)
        {
            if (_lapsed?.Ask.Id == askId)
            {
                _lapsed = null;
                _lapsedHeard = null;
            }

            if (_proposed?.Ask.Id != askId)
            {
                return null;
            }

            var proposal = _proposed;
            _proposed = null;
            _proposedHeard = null;
            _proposalExpiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return proposal;
        }
    }

    private void ExpireProposal()
    {
        ChatAllowProposal? proposal;
        lock (_lock)
        {
            if (_proposed is null)
            {
                return;
            }

            // A timer can fire a little early (its tick is about 15 ms), or the clock can move: wait out the rest.
            var left = Remaining(_proposedHeard is { } heard ? heard + ProposalLifetime : _proposed.At + ReadBackLifetime);
            if (left > TimeSpan.Zero)
            {
                _proposalExpiry.Change(left, Timeout.InfiniteTimeSpan);
                return;
            }

            proposal = _proposed;
            _proposed = null;
            _lapsed = proposal; // a yes said in time and transcribed after this still answers it (ProposalFor)
            (_lapsedHeard, _proposedHeard) = (_proposedHeard, null);
        }

        ProposalEnded?.Invoke(proposal, ChatProposalEnd.Expired);
    }

    /// <summary>How long until <paramref name="end"/>; zero when it is past.</summary>
    private TimeSpan Remaining(DateTimeOffset end) => end - _time.GetUtcNow() is { } left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    /// <summary>Lets a held ask go to VS Code, which asks it in the chat's tab. False when it is not held any more.</summary>
    public bool ToVsCode(string askId) => Close(askId, ChatAskOutcome.ToVsCode, null);

    /// <summary>
    /// The user asked to stop the chat: what its main agent asks ends as <see cref="ChatAskOutcome.Stopped"/>, so the stop
    /// goes back in the ask's answer. Held, the ask would hold the stop too: a stop lands at the chat's next tool step,
    /// and the step that asks is held. A stop is never handed to a sub-agent, so what one asks goes to VS Code: the
    /// sub-agent goes on once it is answered there, and the main agent takes the stop at its next step.
    /// </summary>
    public void Stop(string sessionId)
    {
        CloseWhere(h => h.Ask.SessionId == sessionId && h.Ask.Step.AgentId is null, ChatAskOutcome.Stopped);
        CloseWhere(h => h.Ask.SessionId == sessionId && h.Ask.Step.AgentId is not null, ChatAskOutcome.ToVsCode);
    }

    /// <summary>The window changed: a held ask it does not keep any more (<see cref="Keeps"/>) goes to VS Code.</summary>
    public void Recheck() => CloseWhere(h => !KeepsSafely(h.Ask), ChatAskOutcome.ToVsCode);

    private bool KeepsSafely(ChatAsk ask)
    {
        try
        {
            return Keeps(ask);
        }
        catch (Exception)
        {
            return true; // a window that cannot say leaves the ask where it is
        }
    }

    /// <summary>The asks held now, oldest first.</summary>
    public IReadOnlyList<ChatAsk> Open()
    {
        lock (_lock)
        {
            return _held.Values.Select(h => h.Ask).OrderBy(a => a.At).ToList();
        }
    }

    /// <summary>Whether this ask is held still: one that ended before its <see cref="Opened"/> was handled needs no card.</summary>
    public bool IsHeld(string askId)
    {
        lock (_lock)
        {
            return _held.ContainsKey(askId);
        }
    }

    /// <summary>Whether an ask of this chat is held.</summary>
    public bool Holds(string sessionId)
    {
        lock (_lock)
        {
            return _held.Values.Any(h => h.Ask.SessionId == sessionId);
        }
    }

    /// <summary>
    /// Whether the chat waiting since <paramref name="since"/> waits on an ask taken here, held still or answered already, so
    /// its card tells it and not the news: one asked within <see cref="ExplainWindow"/> of it. A prompt's own
    /// PermissionRequest can land just before the hook that holds it, and its Notification seconds after; the news of it may
    /// be told only after it was answered on its card. A wait that begins long after (a plan to approve while a sub-agent's
    /// prompt is held) is another one, left to VS Code, and news.
    /// </summary>
    public bool Explains(string sessionId, DateTimeOffset since)
    {
        lock (_lock)
        {
            return _taken.Any(t => t.SessionId == sessionId && (t.At - since).Duration() <= ExplainWindow);
        }
    }

    /// <summary>The waiting a held ask means for the chat, as the <c>PermissionRequest</c> VS Code's form brings would tell it.</summary>
    public static HookEvent Asking(ChatAsk ask) => new()
    {
        SessionId = ask.SessionId,
        EventName = "PermissionRequest",
        Signal = SessionSignal.Notification,
        At = ask.At,
        Cwd = ask.Step.Cwd,
        TranscriptPath = ask.Step.TranscriptPath,
        ToolName = ask.Step.ToolName,
        ToolUseId = ask.Step.ToolUseId,
        AgentId = ask.Step.AgentId,
        Message = ask.Questions[0].Text,
        Source = "asked in Raven's panel",
        RelayPid = ask.Step.RelayPid,
        ParentChain = ask.Step.ParentChain,
    };

    /// <summary>
    /// The end of the waiting for an ask whose hook was let go: Claude Code tells no step of its own, and the Yard would show
    /// the chat waiting for an answer nobody asks for any more.
    /// </summary>
    public static HookEvent LetGo(ChatAsk ask, DateTimeOffset at) => new()
    {
        SessionId = ask.SessionId,
        EventName = "PostToolUse",
        Signal = SessionSignal.ToolUse,
        At = at,
        ToolName = ask.Step.ToolName,
        ToolUseId = ask.Step.ToolUseId,
        AgentId = ask.Step.AgentId,
        Source = "question let go",
        RelayPid = ask.Step.RelayPid,
        ParentChain = ask.Step.ParentChain,
    };

    /// <summary>A chat whose turn is over, or that ended, asks nothing any more.</summary>
    private void Changed(SessionChanged change)
    {
        if (change.Current.State is not SessionState.Idle && SessionStateMachine.IsLive(change.Current.State))
        {
            return;
        }

        CloseWhere(h => h.Ask.SessionId == change.Current.SessionId, ChatAskOutcome.Gone);
        lock (_lock)
        {
            foreach (var id in _begun.Where(b => b.Value.SessionId == change.Current.SessionId).Select(b => b.Key).ToList())
            {
                _begun.Remove(id);
            }
        }
    }

    /// <summary>
    /// Where the chats' tool uses are, for the permission prompts held. Claude Code does not let a prompt's hook go when the
    /// prompt is answered in the VS Code tab, so:
    /// <list type="bullet">
    /// <item>The end of the prompt's own tool use (its PostToolUse, by the id it claimed when held) means it was allowed
    /// there. Only its own: tools that run side by side (Grep, WebFetch, read-only MCP tools) take steps while a prompt is
    /// held, and may ask at the same time.</item>
    /// <item>The end of the agent's turn (Stop for the main agent, SubagentStop for a sub-agent, which may run on after the
    /// main turn ends) means it waits on no prompt any more. A plain No there ends the turn with no hook at all; the turn's
    /// end closes the prompt then (<see cref="Changed"/>).</item>
    /// </list>
    /// A prompt denied there with words, whose agent carries on, ends neither: its tab showing no prompt does
    /// (<see cref="Sweep"/>).
    /// </summary>
    private void Moves(HookEvent step)
    {
        switch (step.EventName)
        {
            case "PreToolUse" when step.ToolUseId is { } id:
                lock (_lock)
                {
                    // The same step told twice (an older relay still installed beside this one): the copy with the
                    // input's fingerprint counts.
                    if (_begun.TryGetValue(id, out var told))
                    {
                        if (told.ToolInputHash is null && step.ToolInputHash is not null)
                        {
                            _begun[id] = step;
                        }
                    }
                    else if (_begun.TryAdd(id, step))
                    {
                        _begunOrder.Enqueue(id);
                        while (_begunOrder.Count > BegunKept)
                        {
                            _begun.Remove(_begunOrder.Dequeue());
                        }
                    }
                }

                break;
            case "PostToolUse" or "PostToolUseFailure" when step.ToolUseId is { } id:
                Held? asked;
                lock (_lock)
                {
                    _begun.Remove(id, out var begun);
                    asked = _held.Values.FirstOrDefault(h => h.Ask.Kind == ChatAskKind.Permission && h.Ask.Step.ToolUseId == id);
                    if (asked is null && begun is not null)
                    {
                        _ended.Enqueue((begun, step.At));
                        while (_ended.Count > EndedKept)
                        {
                            _ended.Dequeue();
                        }
                    }
                }

                if (asked is not null)
                {
                    Close(asked.Ask.Id, ChatAskOutcome.AnsweredInVsCode, null);
                }

                break;
            case "SubagentStop":
                CloseWhere(h => h.Ask.Kind == ChatAskKind.Permission && SameAgent(h.Ask, step) && step.At >= h.Ask.At, ChatAskOutcome.Gone);
                break;
            case "Stop":
                CloseWhere(h => h.Ask.Kind == ChatAskKind.Permission && h.Ask.SessionId == step.SessionId && h.Ask.Step.AgentId is null
                    && step.At >= h.Ask.At, ChatAskOutcome.Gone);
                break;
        }
    }

    /// <summary>
    /// Whether the held ask is the permission prompt the tool use <paramref name="begun"/> raised: the same agent, tool and
    /// input, asked after it began. A prompt from a relay without the input's fingerprint is no tool use's: its turn's end
    /// closes it.
    /// </summary>
    private static bool IsPromptOf(ChatAsk ask, HookEvent begun) =>
        ask.Kind == ChatAskKind.Permission && SameAgent(ask, begun) && ask.At >= begun.At
        && string.Equals(ask.Step.ToolName, begun.ToolName, StringComparison.Ordinal)
        && ask.Step.ToolInputHash is { } fingerprint && string.Equals(fingerprint, begun.ToolInputHash, StringComparison.Ordinal);

    /// <summary>
    /// The held permission prompts whose chat's tab shows no prompt any more (<see cref="ShowsPrompt"/>): answered there, in
    /// a way no hook tells. Only those held past <see cref="SettleTime"/>: a tab shows a prompt a moment after it is asked.
    /// </summary>
    private void Sweep()
    {
        List<string> sessions;
        var settled = _time.GetUtcNow() - SettleTime;
        lock (_lock)
        {
            if (!_held.Values.Any(h => h.Ask.Kind == ChatAskKind.Permission))
            {
                _sweep.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            sessions = _held.Values.Where(h => h.Ask.Kind == ChatAskKind.Permission && h.Ask.At <= settled)
                .Select(h => h.Ask.SessionId).Distinct(StringComparer.Ordinal).ToList();
        }

        foreach (var session in sessions)
        {
            bool? shows;
            try
            {
                shows = ShowsPrompt(session);
            }
            catch (Exception)
            {
                shows = null; // a record that cannot be read tells nothing
            }

            if (shows == false)
            {
                CloseWhere(h => h.Ask.Kind == ChatAskKind.Permission && h.Ask.SessionId == session && h.Ask.At <= settled, ChatAskOutcome.AnsweredInVsCode);
            }
        }
    }

    private static bool SameAgent(ChatAsk ask, HookEvent step) =>
        ask.SessionId == step.SessionId && string.Equals(ask.Step.AgentId, step.AgentId, StringComparison.Ordinal);

    /// <summary>Ends the held asks that <paramref name="ends"/> picks; it is asked outside the lock.</summary>
    private void CloseWhere(Func<Held, bool> ends, ChatAskOutcome outcome)
    {
        List<Held> held;
        lock (_lock)
        {
            held = [.. _held.Values];
        }

        foreach (var one in held.Where(ends))
        {
            Close(one.Ask.Id, outcome, null);
        }
    }

    private bool Close(string askId, ChatAskOutcome outcome, IReadOnlyList<string>? answers, ChatPermit? permit = null)
    {
        Held? held;
        lock (_lock)
        {
            if (!_held.Remove(askId, out held))
            {
                return false;
            }
        }

        Finish(held, outcome, answers, permit);
        return true;
    }

    private void Finish(Held held, ChatAskOutcome outcome, IReadOnlyList<string>? answers, ChatPermit? permit = null)
    {
        var closed = new ChatAskClosed(held.Ask, outcome, answers, permit);
        held.Done.TrySetResult(closed);
        Closed?.Invoke(closed);
        // An allow proposed for it waits for no yes any more (a Confirm took its proposal before it got here).
        if (TakeProposal(held.Ask.Id) is { } proposal)
        {
            ProposalEnded?.Invoke(proposal, ChatProposalEnd.Closed);
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _steps.Dispose();
        _sweep.Dispose();
        _proposalExpiry.Dispose();
    }

    private sealed record Held(ChatAsk Ask, TaskCompletionSource<ChatAskClosed> Done);
}
