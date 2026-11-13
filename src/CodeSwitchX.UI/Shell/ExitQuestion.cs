namespace CodeSwitchX.UI.Shell;

/// <summary>What Exit does with the VS Code windows CodeSwitchX hosts (#420).</summary>
public enum ExitChoice
{
    /// <summary>CodeSwitchX closes, and every hosted VS Code window is asked to close too.</summary>
    CloseAll,

    /// <summary>CodeSwitchX closes; VS Code stays open, as after the window's X.</summary>
    CloseOnly,

    Cancel,
}

/// <summary>The words of Exit's question (#420): how many VS Code windows are open, and how many of them still work.</summary>
public static class ExitQuestion
{
    public static string Question(int open, int working)
    {
        var windows = open == 1 ? "1 VS Code window is open" : $"{open} VS Code windows are open";
        return working switch
        {
            0 => $"{windows}.",
            _ when open == 1 => $"{windows}, and a chat in it is still working.",
            1 => $"{windows}, and 1 of them has a chat that is still working.",
            _ => $"{windows}, and {working} of them have a chat that is still working.",
        };
    }

    // Each window closes as by its own X: VS Code asks about unsaved files, except that the last of its windows, with hot exit
    // on (its default), keeps them for the next start instead of asking.
    public static string Detail(int working) =>
        working > 0 ? "Closing them stops those chats mid-turn. Unsaved files are not lost: VS Code asks about them, or keeps them for its next start." : "Unsaved files are not lost: VS Code asks about them, or keeps them for its next start.";
}
