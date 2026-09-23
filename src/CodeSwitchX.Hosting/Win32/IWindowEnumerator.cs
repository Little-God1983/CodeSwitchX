namespace CodeSwitchX.Hosting.Win32;

public interface IWindowEnumerator
{
    /// <summary>Unowned top-level windows, hidden ones included (see <see cref="WindowInfo.IsVisible"/>).</summary>
    IReadOnlyList<WindowInfo> TopLevelWindows();

    /// <summary>Process image name without extension, e.g. "Code"; null when the process is gone or inaccessible.</summary>
    string? ProcessName(uint pid);
}
