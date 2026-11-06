using System.Windows;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>What a chat row's right-click menu does (#386): Compact now, and Keep warm. Called on the UI thread.</summary>
public interface IChatRowMenu
{
    /// <summary>
    /// Compacts the chat now, asking first when that cuts a turn off; done when the compaction is, or was not begun. What
    /// goes wrong is shown to the user. Never throws.
    /// </summary>
    Task CompactAsync(string sessionId);

    /// <summary>Whether the chat is being compacted right now.</summary>
    bool IsCompacting(string sessionId);

    /// <summary>Until when the chat is kept warm; null for one that is not.</summary>
    DateTimeOffset? KeptWarmUntil(string sessionId);

    /// <summary>Why the chat cannot be kept warm, in words for the user; null when it can.</summary>
    string? WhyNotKeptWarm(string sessionId);

    /// <summary>Keeps the chat warm for that long from now.</summary>
    void KeepWarm(string sessionId, TimeSpan duration);

    void StopKeepingWarm(string sessionId);
}

/// <summary>The Yard's own questions and messages to the user, in a box over the window.</summary>
public interface IYardDialogs
{
    /// <summary>Asks a yes-or-no question; whether the user said yes.</summary>
    bool Confirm(string question);

    /// <summary>Tells the user what went wrong.</summary>
    void Tell(string message);
}

/// <summary>Message boxes over the app's window.</summary>
public sealed class YardDialogs : IYardDialogs
{
    public bool Confirm(string question) =>
        Show(question, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Tell(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static MessageBoxResult Show(string text, MessageBoxButton buttons, MessageBoxImage image) =>
        Application.Current?.MainWindow is { IsLoaded: true } window
            ? MessageBox.Show(window, text, "CodeSwitchX", buttons, image, MessageBoxResult.No)
            : MessageBox.Show(text, "CodeSwitchX", buttons, image, MessageBoxResult.No);
}

/// <summary>
/// The menu's work, done by what Raven's tools use too: the compaction is the one "compact the … chat" starts (#226), with
/// the same question before a turn is cut off, and keeping warm is <see cref="Core.Yard.KeepWarm"/>'s. The Yard's directory
/// and actions are asked for when first needed: both are made after the Yard that owns the rows.
/// </summary>
public sealed class ChatRowMenu(Func<IYardDirectory> yard, Func<IYardActions> actions, KeepWarm keepWarm, IYardDialogs dialogs, ILogger<ChatRowMenu> logger)
    : IChatRowMenu
{
    public async Task CompactAsync(string sessionId)
    {
        var title = "";
        try
        {
            // Off the UI thread: the directory reads the board on it, and must not wait for the thread it is called on.
            var chats = await Task.Run(() => yard().ChatsAsync(CancellationToken.None)).ConfigureAwait(true);
            if (chats.FirstOrDefault(c => string.Equals(c.Id, sessionId, StringComparison.OrdinalIgnoreCase)) is not { } chat)
            {
                dialogs.Tell("The chat is no longer on the Yard, so it was not compacted.");
                return;
            }

            title = chat.Title;
            if ((chat.State == SessionState.Working || chat.NeedsYou)
                && !dialogs.Confirm($"The {chat.Title} chat is {(chat.NeedsYou ? "waiting for you in the middle of its turn" : "still working")}. "
                    + "Compacting it now cuts that turn off.\n\nCompact it anyway?"))
            {
                return;
            }

            await actions().CompactChatNowAsync(chat, CancellationToken.None).ConfigureAwait(true);
        }
        catch (YardActionException refused)
        {
            dialogs.Tell(refused.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Compacting chat {Id} from the Yard failed", sessionId);
            dialogs.Tell(title.Length > 0 ? $"Compacting the {title} chat failed. The log says why." : "Compacting the chat failed. The log says why.");
        }
    }

    public bool IsCompacting(string sessionId) => actions().IsCompacting(sessionId);

    public DateTimeOffset? KeptWarmUntil(string sessionId) => keepWarm.Until(sessionId);

    public string? WhyNotKeptWarm(string sessionId) => keepWarm.WhyNot(sessionId);

    public void KeepWarm(string sessionId, TimeSpan duration)
    {
        // What the menu showed may be seconds old: the tab closed, or the cache ran out, since.
        if (keepWarm.Start(sessionId, duration) is { } why)
        {
            dialogs.Tell(why);
        }
    }

    public void StopKeepingWarm(string sessionId) => keepWarm.Stop(sessionId);
}
