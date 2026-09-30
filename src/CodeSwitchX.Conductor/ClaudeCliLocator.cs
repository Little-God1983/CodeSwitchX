namespace CodeSwitchX.Conductor;

/// <summary>
/// Finds the <c>claude.exe</c> to run the brain with. Started directly, never through the npm <c>claude.cmd</c> shim:
/// a batch file needs cmd.exe in between, which gets in the way of the standard input and of killing the process. The
/// places, in order: <c>claude.exe</c> on the PATH (the native installer), the exe the npm shim on the PATH runs,
/// the native installer's <c>~\.local\bin</c>, and the ones the VS Code extension bundles. Of those found, the newest
/// by its file version wins: an install on the PATH that has not updated may be too old for the model set (CLI 2.1.260
/// refuses <c>claude-opus-5-5</c>) while the extension carries a newer one. Among equal versions, or where none can be
/// read, the first found in that order.
/// </summary>
/// <param name="versionOf">The version of an exe; null when it has none to read.</param>
public sealed class ClaudeCliLocator(string? path, string homeDirectory, Func<string, bool> fileExists, Func<string, string, IEnumerable<string>> directories,
    Func<string, Version?>? versionOf = null)
{
    public static ClaudeCliLocator Default() => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        File.Exists,
        ExtensionFolders,
        FileVersion);

    /// <summary>Claude Code's exe carries its version as the file's product version ("2.1.285.0").</summary>
    private static Version? FileVersion(string exe)
    {
        try
        {
            return Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Read whole here, so a folder that cannot be listed (its ACL, a redirected profile) is no folder instead of an exception later.</summary>
    private static IEnumerable<string> ExtensionFolders(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, pattern).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>The full path, or null when Claude Code is not installed in any of the places.</summary>
    public string? Find()
    {
        var found = Candidates().Where(fileExists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count == 0)
        {
            return null;
        }

        var zero = new Version(0, 0);
        return found
            .Select((exe, order) => (Exe: exe, Order: order, Version: versionOf?.Invoke(exe) ?? zero))
            .OrderByDescending(c => c.Version)
            .ThenBy(c => c.Order)
            .First().Exe;
    }

    private IEnumerable<string> Candidates()
    {
        var folders = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Trim('"'))
            .Where(f => f.Length > 0)
            .ToList();
        foreach (var folder in folders)
        {
            yield return Path.Combine(folder, "claude.exe");
        }

        foreach (var folder in folders.Where(f => fileExists(Path.Combine(f, "claude.cmd"))))
        {
            yield return Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        }

        yield return Path.Combine(homeDirectory, ".local", "bin", "claude.exe");

        var extensions = Path.Combine(homeDirectory, ".vscode", "extensions");
        foreach (var extension in directories(extensions, "anthropic.claude-code-*").OrderByDescending(VersionOf))
        {
            yield return Path.Combine(extension, "resources", "native-binary", "claude.exe");
        }
    }

    /// <summary>The version in <c>anthropic.claude-code-2.1.285-win32-x64</c>; zero for a folder that names none.</summary>
    private static Version VersionOf(string folder)
    {
        var name = Path.GetFileName(folder)["anthropic.claude-code-".Length..];
        var end = name.IndexOf('-');
        return Version.TryParse(end < 0 ? name : name[..end], out var version) ? version : new Version(0, 0);
    }
}
