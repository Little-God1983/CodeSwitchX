namespace CodeSwitchX.UI.Shell;

/// <summary>What becomes of a window's open cards when its workspace is to be removed (#135).</summary>
public enum RemoveChoice
{
    /// <summary>Nothing is removed: the panel shows the window's chat with its cards.</summary>
    AnswerFirst,

    /// <summary>Each card goes to its chat's VS Code tab, then the workspace is removed.</summary>
    LeaveToVsCode,

    Cancel,
}

/// <summary>The size state of CodeSwitchX's own window.</summary>
public enum ShellWindowState
{
    Normal,
    Minimized,
    Maximized,
}

/// <summary>
/// CodeSwitchX's own window, as Raven changes it by voice (#117); the main window, a fake in tests. A change goes through
/// the window's state, so its StateChanged follows as after a click on the title bar: the Cab's VS Code window goes along.
/// </summary>
public interface IShellWindow
{
    ShellWindowState State { get; }

    /// <summary>The state it was in before it was last minimized, normal or maximized: what restoring it goes back to.</summary>
    ShellWindowState Restored { get; }

    /// <summary>Whether it is in front now: it, or the VS Code window its Cab shows, which holds the focus there.</summary>
    bool IsInFront { get; }

    void Minimize();

    /// <summary>Puts it in <paramref name="state"/> (normal or maximized) and brings it to the front, as far as Windows lets it.</summary>
    void Show(ShellWindowState state);

    /// <summary>
    /// A dialog of its own that is up (Add workspace), or 0: the Cab shows VS Code right under it, so a workspace opened
    /// while it is up, by Raven say, no longer hides it until the next click (#215).
    /// </summary>
    nint Dialog { get; }
}
