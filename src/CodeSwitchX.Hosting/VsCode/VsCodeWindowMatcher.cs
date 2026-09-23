using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.VsCode;

public static class VsCodeWindowMatcher
{
    public const string ElectronClass = "Chrome_WidgetWin_1";
    public const string ProcessName = "Code";
    private const string TitleSuffix = "Visual Studio Code";

    public static WindowInfo? FindNew(IReadOnlyList<WindowInfo> before, IReadOnlyList<WindowInfo> after, string displayName, Func<uint, string?> processName)
    {
        var known = before.Select(w => w.Hwnd).ToHashSet();
        var candidates = after
            .Where(w => !known.Contains(w.Hwnd))
            .Where(w => string.Equals(w.ClassName, ElectronClass, StringComparison.Ordinal))
            .Where(w => TitleNamesWorkspace(w.Title, displayName))
            .Where(w => string.Equals(processName(w.ProcessId), ProcessName, StringComparison.OrdinalIgnoreCase))
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
