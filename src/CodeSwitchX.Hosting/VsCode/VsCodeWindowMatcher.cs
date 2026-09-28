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

    /// <summary>
    /// True when the name stands where VS Code writes the folder name: "${activeEditorShort} - ${rootName} -
    /// ${profileName} - ${appName}", each part only when there is one. The profile is known only when the workspace
    /// sets one; VS Code may also remember one for the folder. Elsewhere the name can be an editor tab ("Dockerfile"),
    /// a profile ("Python") or part of a longer folder name ("App - Copy"), so a match there is only a guess.
    /// </summary>
    public static bool TitleNamesRoot(string title, string displayName, string? profile)
    {
        var segments = Segments(title);
        var name = Segments(displayName);
        if (segments.Length < 2 || name.Length == 0 || !segments[^1].Contains(TitleSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[][] between = string.IsNullOrWhiteSpace(profile) ? [[]] : [[], Segments(profile)];
        foreach (var tail in between)
        {
            var end = segments.Length - 1 - tail.Length;
            var start = end - name.Length;
            if (start is 0 or 1 && RunAt(segments, start, name) && RunAt(segments, end, tail))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Two names one of which is whole segments of the other ("App" and "App - Copy"): a title can name both.</summary>
    public static bool NamesOverlap(string first, string second) =>
        TitleNamesWorkspace(first, second) || TitleNamesWorkspace(second, first);

    private static List<WindowInfo> Matches(IEnumerable<WindowInfo> windows, string displayName, Func<uint, string?> processName) =>
        windows
            .Where(w => string.Equals(w.ClassName, ElectronClass, StringComparison.Ordinal))
            .Where(w => TitleNamesWorkspace(w.Title, displayName))
            .Where(w => string.Equals(processName(w.ProcessId), ProcessName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => TitleNamesRoot(w.Title, displayName, profile: null) ? 0 : 1)
            .ThenBy(w => w.Title.Contains(TitleSuffix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
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

        var segments = Segments(title);
        var name = Segments(displayName);
        for (var start = 0; start + name.Length <= segments.Length; start++)
        {
            if (RunAt(segments, start, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The " - " separated parts, without the "●" VS Code puts in front of a title with unsaved changes.</summary>
    private static string[] Segments(string text) =>
        text.Split(Separator, StringSplitOptions.TrimEntries).Select(s => s.TrimStart('●', ' ')).ToArray();

    private static bool RunAt(string[] segments, int start, string[] run) =>
        start >= 0 && start + run.Length <= segments.Length
        && run.Select((part, i) => string.Equals(segments[start + i], part, StringComparison.OrdinalIgnoreCase)).All(equal => equal);
}
