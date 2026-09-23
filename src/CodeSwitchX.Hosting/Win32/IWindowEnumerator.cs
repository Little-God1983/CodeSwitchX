namespace CodeSwitchX.Hosting.Win32;

public interface IWindowEnumerator
{
    /// <summary>Visible, unowned top-level windows.</summary>
    IReadOnlyList<WindowInfo> TopLevelWindows();

    /// <summary>Process image name without extension, e.g. "Code"; null when the process is gone or inaccessible.</summary>
    string? ProcessName(uint pid);
}
