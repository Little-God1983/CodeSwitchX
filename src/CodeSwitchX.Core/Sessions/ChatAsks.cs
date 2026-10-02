using CodeSwitchX.Core.Messaging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>What a chat can ask the user that CodeSwitchX can answer. Permission prompts are to follow (#102).</summary>
public enum ChatAskKind
{
    /// <summary>Claude's <c>AskUserQuestion</c>: one to four questions, each with options or an answer of the user's own.</summary>
    Question,
}

/// <summary>One option of a question, as the chat wrote it.</summary>
public sealed record ChatQuestionOption(string Label, string? Description);

/// <summary>One question a chat asks; <see cref="MultiSelect"/> lets the user pick more than one option.</summary>
public sealed record ChatQuestion(string Text, string? Header, IReadOnlyList<ChatQuestionOption> Options, bool MultiSelect);

/// <summary>
/// What a chat asks, held by its hook while the user answers in CodeSwitchX. <see cref="Step"/> is the hook event of the
/// tool use that asks (its <c>PreToolUse</c>): the chat, its agent and the tool use's id.
/// </summary>
public sealed record ChatAsk(string Id, ChatAskKind Kind, HookEvent Step, IReadOnlyList<ChatQuestion> Questions)
{
    public string SessionId => Step.SessionId;

    public DateTimeOffset At => Step.At;

    /// <summary>The questions as Raven's brain reads them: "\"Which fruit?\" (one of: Apple, Banana, Cherry)", joined by "; ".</summary>
    public string Describe() => string.Join("; ", Questions.Select(q => $"\"{q.Text}\""
        + (q.Options.Count > 0 ? $" ({(q.MultiSelect ? "any of" : "one of")}: {string.Join(", ", q.Options.Select(o => o.Label))})" : "")));
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
}

/// <summary>A held ask that ended, with the answers given, one per question, when it was <see cref="ChatAskOutcome.Answered"/>.</summary>
public sealed record ChatAskClosed(ChatAsk Ask, ChatAskOutcome Outcome, IReadOnlyList<string>? Answers);

/// <summary>
/// What chats ask the user, held while the user answers in CodeSwitchX. A chat's hook relay hands the ask over and waits
/// (<see cref="HoldAsync"/>): the answers given here (<see cref="Answer"/>) go back to Claude Code as the tool's input, and
/// an ask let go (<see cref="ToVsCode"/>, or after <see cref="Lifetime"/>) leaves the chat's tab to ask it as it always
/// does. Whether an ask is taken at all is <see cref="Takes"/>'s to say; one not taken goes to VS Code at once. While an
/// ask is held, the chat shows as waiting for the user, as it would with VS Code's own form open. Thread-safe: hooks, the
/// bus and the window come on any thread.
/// </summary>
public sealed class ChatAsks : IDisposable
{
    /// <summary>An ask held this long goes to VS Code: the user is not answering it here. Below the hook's own timeout.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);

    public ChatAsks(IEventBus bus, TimeProvider time)
    {
        _bus = bus;
        _time = time;
        _subscription = bus.Subscribe<SessionChanged>(Changed);
    }

    /// <summary>Whether CodeSwitchX takes this ask, or leaves it to VS Code at once. Takes none until the app says otherwise.</summary>
    public Func<ChatAsk, bool> Takes { get; set; } = _ => false;

    /// <summary>
    /// Whether a held ask stays here when the window changes (<see cref="Recheck"/>), or goes to VS Code: the user may not
    /// see it here any more, or may be looking at the chat's own tab. Keeps every one until the app says otherwise.
    /// </summary>
    public Func<ChatAsk, bool> Keeps { get; set; } = _ => true;

    /// <summary>An ask is held now. Raised on the hook's thread.</summary>
    public event Action<ChatAsk>? Opened;

    /// <summary>A held ask ended, however it did. Raised on the thread that ended it.</summary>
    public event Action<ChatAskClosed>? Closed;

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
            takes = ask.Questions.Count > 0 && Takes(ask);
        }
        catch (Exception)
        {
            takes = false; // a window that cannot say leaves the ask where it always was
        }

        if (!takes)
        {
            return null;
        }

        var held = new Held(ask, new TaskCompletionSource<ChatAskClosed>(TaskCreationOptions.RunContinuationsAsynchronously));
        Held? replaced;
        lock (_lock)
        {
            _held.Remove(ask.Id, out replaced);
            _held[ask.Id] = held;
        }

        if (replaced is not null)
        {
            Finish(replaced, ChatAskOutcome.Gone, null);
        }

        // The chat waits for the user from now on, as it does with VS Code's own form open.
        _bus.Publish(new HookEventReceived(Asking(ask)));
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
            // Claude Code let the hook go (the turn was stopped in its tab): nobody waits for the answer any more.
            if (Close(ask.Id, ChatAskOutcome.Gone, null))
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

    /// <summary>Lets a held ask go to VS Code, which asks it in the chat's tab. False when it is not held any more.</summary>
    public bool ToVsCode(string askId) => Close(askId, ChatAskOutcome.ToVsCode, null);

    /// <summary>
    /// The user asked to stop the chat: what its main agent asks ends as <see cref="ChatAskOutcome.Stopped"/>, so the stop
    /// goes back in the ask's answer. Held, the ask would hold the stop too: a stop lands at the chat's next tool step,
    /// and the step that asks is held. A sub-agent's ask stays, as a stop is never handed to a sub-agent.
    /// </summary>
    public void Stop(string sessionId) => CloseWhere(h => h.Ask.SessionId == sessionId && h.Ask.Step.AgentId is null, ChatAskOutcome.Stopped);

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

    /// <summary>Whether an ask of this chat is held: its waiting is told here, not as news.</summary>
    public bool Holds(string sessionId)
    {
        lock (_lock)
        {
            return _held.Values.Any(h => h.Ask.SessionId == sessionId);
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
    }

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

    private bool Close(string askId, ChatAskOutcome outcome, IReadOnlyList<string>? answers)
    {
        Held? held;
        lock (_lock)
        {
            if (!_held.Remove(askId, out held))
            {
                return false;
            }
        }

        Finish(held, outcome, answers);
        return true;
    }

    private void Finish(Held held, ChatAskOutcome outcome, IReadOnlyList<string>? answers)
    {
        var closed = new ChatAskClosed(held.Ask, outcome, answers);
        held.Done.TrySetResult(closed);
        Closed?.Invoke(closed);
    }

    public void Dispose() => _subscription.Dispose();

    private sealed record Held(ChatAsk Ask, TaskCompletionSource<ChatAskClosed> Done);
}
