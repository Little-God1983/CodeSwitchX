using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.VsCode;

public static class VsCodeWindowMatcher
{
    public const string ElectronClass = "Chrome_WidgetWin_1";
    public const string ProcessName = "Code";
    private const string TitleSuffix = "Visual Studio Code";

    /// <summary>
    /// A VS Code window created after <paramref name="before"/> was captured that names the workspace. Only windows
    /// VS Code has already shown count: adopting one it is still setting up would race its own ShowWindow.
    /// </summary>
    public static WindowInfo? FindNew(IReadOnlyList<WindowInfo> before, IReadOnlyList<WindowInfo> after, string displayName, Func<uint, string?> processName)
    {
        var known = before.Select(w => w.Hwnd).ToHashSet();
        return Pick(after.Where(w => w.IsVisible && !known.Contains(w.Hwnd)), displayName, processName);
    }

    /// <summary>
    /// A VS Code window that already shows the workspace (VS Code is single-instance and focuses it instead of opening
    /// a new one). Hidden windows count too: that is how a window hidden by a crashed instance gets back to the user.
    /// </summary>
    public static WindowInfo? FindExisting(IReadOnlyList<WindowInfo> windows, string displayName, Func<uint, string?> processName) =>
        Pick(windows, displayName, processName);

    private static WindowInfo? Pick(IEnumerable<WindowInfo> windows, string displayName, Func<uint, string?> processName)
    {
        var candidates = windows
            .Where(w => string.Equals(w.ClassName, ElectronClass, StringComparison.Ordinal))
            .Where(w => TitleNamesWorkspace(w.Title, displayName))
            .Where(w => string.Equals(processName(w.ProcessId), ProcessName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.IsVisible ? 0 : 1)
            .ToList();

        return candidates.FirstOrDefault(w => w.Title.Contains(TitleSuffix, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// VS Code titles are " - " separated segments ("● Program.cs - App - Visual Studio Code"). The workspace name must
    /// be a whole segment, otherwise "App" would also claim the "App2" window.
    /// </summary>
    internal static bool TitleNamesWorkspace(string title, string displayName)
    {
        if (title.Length == 0)
        {
            return false;
        }

        foreach (var segment in title.Split(" - ", StringSplitOptions.TrimEntries))
        {
            if (string.Equals(segment.TrimStart('●', ' '), displayName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
