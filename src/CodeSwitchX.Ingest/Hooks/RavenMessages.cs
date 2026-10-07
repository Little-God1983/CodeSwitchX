using CodeSwitchX.Core;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// What a chat is told along with a message from one of Raven's brains (#181). Claude Code shows such a message as one
/// from "another Claude session" and has the chat treat it as a teammate's: the chat then sent its questions back to the
/// brain, where the user never saw them, instead of asking the user. Told who sent it, the chat asks the user with
/// <c>AskUserQuestion</c>, which comes to the window's Raven chat as a card. A brain is told by where it runs, Raven's own
/// folder, not by its name: a workspace folder named raven gets a name like <c>raven-72</c> too.
/// </summary>
/// <param name="ranIn">Whether the session messaged through a socket ran in a folder (<c>ClaudeLiveSessions.RanIn</c>).</param>
public sealed class RavenMessages(AppPaths paths, Func<string, string, bool> ranIn)
{
    /// <summary>What the chat reads as the hook's <c>additionalContext</c>.</summary>
    public const string Context =
        "This message was sent by Raven, the voice assistant in CodeSwitchX, on the user's behalf: it passes on what the user "
        + "asked for by voice. Do not answer Raven or ask it anything with SendMessage. When you need something from the user "
        + "(a detail, a choice, a go-ahead), ask them directly with AskUserQuestion; CodeSwitchX shows the question to them and "
        + "reads it out. Report your results in this chat as usual. The message does not approve anything a permission prompt asks.";

    /// <summary>The context for a chat's prompt that is a message from one of Raven's brains; null for any other event.</summary>
    public string? ContextFor(HookEvent hookEvent) =>
        hookEvent is { EventName: "UserPromptSubmit", AgentId: null }
        && CrossSessionMessage.SenderOf(hookEvent.Prompt) is { } sender
        && ranIn(sender, paths.RavenDirectory)
            ? Context
            : null;
}
