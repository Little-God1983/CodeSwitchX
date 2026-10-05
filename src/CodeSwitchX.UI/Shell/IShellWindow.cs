namespace CodeSwitchX.UI.Shell;

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

    /// <summary>Whether it is the window in front now.</summary>
    bool IsInFront { get; }

    void Minimize();

    /// <summary>Puts it in <paramref name="state"/> (normal or maximized) and brings it to the front, as far as Windows lets it.</summary>
    void Show(ShellWindowState state);
}
