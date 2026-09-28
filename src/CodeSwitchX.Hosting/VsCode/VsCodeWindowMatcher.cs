using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.VsCode;

public static class VsCodeWindowMatcher
{
    public const string ElectronClass = "Chrome_WidgetWin_1";
    public const string ProcessName = "Code";
    private const string TitleSuffix = "Visual Studio Code";
    private const string Separator = " - ";

    /// <summary>
    /// A VS Code window created after <paramref name="before"/> was captured that names the workspace. Only windows
    /// VS Code has already shown count: adopting one it is still setting up would race its own ShowWindow.
    /// </summary>
    public static WindowInfo? FindNew(IReadOnlyList<WindowInfo> before, IReadOnlyList<WindowInfo> after, string displayName, Func<uint, string?> processName)
    {
        var known = before.Select(w => w.Hwnd).ToHashSet();
        return Matches(after.Where(w => w.IsVisible && !known.Contains(w.Hwnd)), displayName, processName).FirstOrDefault();
    }

    /// <summary>
    /// A VS Code window that already shows the workspace (VS Code is single-instance and focuses it instead of opening
    /// a new one). Hidden windows count too: that is how a window hidden by a crashed instance gets back to the user.
    /// </summary>
    public static WindowInfo? FindExisting(IReadOnlyList<WindowInfo> windows, string displayName, Func<uint, string?> processName) =>
        Matches(windows, displayName, processName).FirstOrDefault();

    /// <summary>Every window <see cref="FindExisting"/> would consider, best first.</summary>
    public static IReadOnlyList<WindowInfo> FindAllExisting(IReadOnlyList<WindowInfo> windows, string displayName, Func<uint, string?> processName) =>
        Matches(windows, displayName, processName);

    /// <summary>A VS Code window that shows a folder or workspace: its title names one before the app name.</summary>
    public static bool ShowsAFolder(WindowInfo window, Func<uint, string?> processName) =>
        string.Equals(window.ClassName, ElectronClass, StringComparison.Ordinal)
        && window.Title.Contains(Separator, StringComparison.Ordinal)
        && window.Title.Contains(TitleSuffix, StringComparison.OrdinalIgnoreCase)
        && string.Equals(processName(window.ProcessId), ProcessName, StringComparison.OrdinalIgnoreCase);

    private static List<WindowInfo> Matches(IEnumerable<WindowInfo> windows, string displayName, Func<uint, string?> processName) =>
        windows
            .Where(w => string.Equals(w.ClassName, ElectronClass, StringComparison.Ordinal))
            .Where(w => TitleNamesWorkspace(w.Title, displayName))
            .Where(w => string.Equals(processName(w.ProcessId), ProcessName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Title.Contains(TitleSuffix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(w => w.IsVisible ? 0 : 1)
            .ToList();

    /// <summary>
    /// VS Code titles are " - " separated segments ("● Program.cs - App - Visual Studio Code"). The workspace name must
    /// be whole segments, otherwise "App" would also claim the "App2" window. A name can span several segments: a
    /// folder copied in Explorer is called "App - Copy", and VS Code writes that into the title as it is.
    /// </summary>
    internal static bool TitleNamesWorkspace(string title, string displayName)
    {
        if (title.Length == 0 || displayName.Length == 0)
        {
            return false;
        }

        var segments = title.Split(Separator, StringSplitOptions.TrimEntries).Select(s => s.TrimStart('●', ' ')).ToArray();
        var name = displayName.Split(Separator, StringSplitOptions.TrimEntries);
        for (var start = 0; start + name.Length <= segments.Length; start++)
        {
            if (name.Select((part, i) => string.Equals(segments[start + i], part, StringComparison.OrdinalIgnoreCase)).All(equal => equal))
            {
                return true;
            }
        }

        return false;
    }
}
