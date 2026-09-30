namespace CodeSwitchX.Conductor;

/// <summary>
/// Finds the <c>claude.exe</c> to run the brain with. Started directly, never through the npm <c>claude.cmd</c> shim:
/// a batch file needs cmd.exe in between, which gets in the way of the standard input and of killing the process. The
/// places, first found wins: <c>claude.exe</c> on the PATH (the native installer), the exe the npm shim on the PATH runs,
/// the native installer's <c>~\.local\bin</c>, and the newest one the VS Code extension bundles.
/// </summary>
public sealed class ClaudeCliLocator(string? path, string homeDirectory, Func<string, bool> fileExists, Func<string, string, IEnumerable<string>> directories)
{
    public static ClaudeCliLocator Default() => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        File.Exists,
        (folder, pattern) => Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, pattern) : []);

    /// <summary>The full path, or null when Claude Code is not installed in any of the places.</summary>
    public string? Find() => Candidates().FirstOrDefault(fileExists);

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
