using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.Input;
using Path = System.IO.Path;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// A repository that carries the stable-build scripts (#423): <c>scripts\build.ps1</c> next to a <c>scripts\_common.ps1</c>
/// that names the Start Menu shortcut the build writes. The tile's menu builds it and starts it.
/// </summary>
/// <param name="RepoPath">The repository's top folder, where its scripts folder is.</param>
/// <param name="ShortcutName">The shortcut's bare file name in the Start Menu's Programs folder, e.g. "Some App.lnk".</param>
/// <param name="ReleaseBranch">The branch the script builds from (<c>$script:ReleaseBranch</c>); null when the file names none.</param>
public sealed partial record StableBuildRepo(string RepoPath, string ShortcutName, string? ReleaseBranch = null)
{
    public string BuildScript => Path.Combine(RepoPath, "scripts", "build.ps1");

    /// <summary>
    /// The scripts of the repository <paramref name="folder"/> is in, looked for at its top; null when it has none, or they
    /// cannot be read. A lookup that goes wrong in any way only means no entries: it must not cost the tile its git round.
    /// </summary>
    public static StableBuildRepo? Find(string folder)
    {
        try
        {
            var top = TopOf(folder);
            var scripts = Path.Combine(top, "scripts");
            var common = Path.Combine(scripts, "_common.ps1");
            if (!File.Exists(Path.Combine(scripts, "build.ps1")) || !File.Exists(common))
            {
                return null;
            }

            var text = File.ReadAllText(common);
            if (Setting(text, "ShortcutName") is not { } shortcut
                || shortcut != Path.GetFileName(shortcut) || !shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // Only a bare shortcut name: Run opens what it names, and must not open a program from elsewhere.
                return null;
            }

            return new StableBuildRepo(top, shortcut, Setting(text, "ReleaseBranch"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The folder itself or the nearest one above it with a .git (a folder, or a file in a worktree): the repository's top.</summary>
    private static string TopOf(string folder)
    {
        if (HasGit(folder))
        {
            return folder;
        }

        for (var dir = new DirectoryInfo(folder).Parent; dir is not null; dir = dir.Parent)
        {
            if (HasGit(dir.FullName))
            {
                return dir.FullName;
            }
        }

        return folder;

        static bool HasGit(string dir) => Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));
    }

    /// <summary>
    /// The setting's value as the file writes it; null when it is not there, or is a double-quoted string PowerShell would
    /// fill in (a $ or a backtick in it): its text is then not the value the script uses.
    /// </summary>
    private static string? Setting(string text, string name)
    {
        if (SettingLine().Matches(text).FirstOrDefault(m => string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase)) is not { } match)
        {
            return null;
        }

        if (match.Groups["single"].Success)
        {
            return match.Groups["single"].Value is { Length: > 0 } single ? single : null;
        }

        var value = match.Groups["double"].Value;
        return value.Length > 0 && value.IndexOfAny(['$', '`']) < 0 ? value : null;
    }

    /// <summary><c>$script:Name = 'value'</c> or <c>"value"</c> at a line's start; PowerShell takes the scope in any casing.</summary>
    [GeneratedRegex("""^\s*\$script:(?<name>\w+)\s*=\s*(?:'(?<single>[^']*)'|"(?<double>[^"]*)")""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SettingLine();
}

/// <summary>Whether a line's git state lets a stable build start (#423): the user's precheck, before the script's own stricter ones.</summary>
public static class StableBuildCheck
{
    /// <summary>Null when the build may start; otherwise why not, for the greyed entry's tooltip.</summary>
    public static string? BuildBlock(GitLine line)
    {
        if (line.Branch is null || line.DirtyCount is null)
        {
            return "Git state unknown";
        }

        // The branch the script names is the one it builds from; a file that names none falls back to the user's main or develop.
        if (line.StableBuild?.ReleaseBranch is { } release)
        {
            if (line.Branch != release)
            {
                return $"On {line.Branch} - this repository's stable build runs from {release}";
            }
        }
        else if (line.Branch is not ("main" or "develop"))
        {
            return $"On {line.Branch} - a stable build runs from main or develop";
        }

        return line.DirtyCount switch
        {
            0 => null,
            1 => "1 uncommitted change - commit or stash first",
            var n => $"{n} uncommitted changes - commit or stash first",
        };
    }
}

/// <summary>Starts a stable build in a window of its own, and the installed build through its Start Menu shortcut (#423).</summary>
public interface IStableBuildLauncher
{
    /// <summary>Where the build writes the shortcut of that name: the Start Menu's Programs folder.</summary>
    string ShortcutPath(string shortcutName);

    bool ShortcutExists(string shortcutPath);

    /// <summary>Runs the repository's build.ps1 in a new terminal window that stays open; false when no window could be started.</summary>
    bool StartBuild(StableBuildRepo repo);

    /// <summary>Opens the shortcut as Explorer would; false when it could not be opened.</summary>
    bool Run(string shortcutPath);
}

/// <summary>
/// The build runs in Windows Terminal, else in a console of its own, never inside the app: CodeSwitchX's own build closes
/// CodeSwitchX, and the window has to outlive it. -NoExit keeps the output on screen however the build ends.
/// </summary>
public sealed class StableBuildLauncher : IStableBuildLauncher
{
    public string ShortcutPath(string shortcutName) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), shortcutName);

    public bool ShortcutExists(string shortcutPath) => File.Exists(shortcutPath);

    public bool StartBuild(StableBuildRepo repo)
    {
        var shell = OnPath("pwsh.exe") ? "pwsh.exe" : "powershell.exe";
        return ShellStart.TryStart(new ProcessStartInfo("wt.exe", TerminalArguments(repo, shell)))
            || ShellStart.TryStart(new ProcessStartInfo(shell, ShellArguments(repo.BuildScript)) { WorkingDirectory = Path.TrimEndingDirectorySeparator(repo.RepoPath) });
    }

    /// <summary>What wt.exe is given: a new window in the repository, running the build there.</summary>
    internal static string TerminalArguments(StableBuildRepo repo, string shell)
    {
        // No separator before the closing quote, which would escape it: a drive root goes as "E:\." (E: alone is drive-relative).
        // A bare ; starts wt's next command, so it goes as \; in both paths.
        var folder = Path.TrimEndingDirectorySeparator(repo.RepoPath);
        var quoted = Path.EndsInDirectorySeparator(folder) ? folder + "." : folder;
        return $"-d \"{quoted.Replace(";", "\\;")}\" {shell} {ShellArguments(repo.BuildScript.Replace(";", "\\;"))}";
    }

    private static string ShellArguments(string script) => $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{script}\"";

    public bool Run(string shortcutPath) => ShellStart.TryStart(new ProcessStartInfo(shortcutPath));

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(dir => File.Exists(Path.Combine(dir, exe)));
}

/// <summary>One repository's pair of entries on a tile's menu (#423), made when the menu opens.</summary>
public sealed partial class StableBuildEntry
{
    private readonly StableBuildRepo _repo;
    private readonly string _shortcutPath;
    private readonly IStableBuildLauncher _launcher;
    private readonly IYardDialogs _dialogs;

    /// <param name="line">A line with stable-build scripts; its git state is what the precheck reads.</param>
    /// <param name="named">The tile has several such repositories: the entries say which.</param>
    public StableBuildEntry(GitLine line, bool named, IStableBuildLauncher launcher, IYardDialogs dialogs)
    {
        var repo = line.StableBuild ?? throw new ArgumentException("The line has no stable-build scripts.", nameof(line));
        _repo = repo;
        _launcher = launcher;
        _dialogs = dialogs;
        var folder = Path.TrimEndingDirectorySeparator(repo.RepoPath);
        Name = Path.GetFileName(folder) is { Length: > 0 } name ? name : folder; // a drive root has no folder name: "E:\"
        BuildHeader = named ? $"Build {Name}" : "Build new version";
        RunHeader = named ? $"Run {Name}" : "Run";
        BuildBlock = StableBuildCheck.BuildBlock(line);
        _shortcutPath = launcher.ShortcutPath(repo.ShortcutName);
        RunBlock = launcher.ShortcutExists(_shortcutPath) ? null : "No stable build yet";
    }

    /// <summary>The repository's folder name: what the dialog and the headers call the app.</summary>
    public string Name { get; }
    public string BuildHeader { get; }
    public string RunHeader { get; }
    public string? BuildBlock { get; }
    public string? RunBlock { get; }
    public bool CanBuild => BuildBlock is null;
    public bool CanRun => RunBlock is null;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private void Build()
    {
        if (!_dialogs.Confirm($"Build a new version of {Name}?\n\nThe build script bumps the version, commits and pushes it, and may close the running {Name} and start the new one. Its window shows every step."))
        {
            return;
        }

        if (!_launcher.StartBuild(_repo))
        {
            _dialogs.Tell($"Could not start the build of {Name}.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (!_launcher.Run(_shortcutPath))
        {
            _dialogs.Tell($"Could not start {Name}.");
        }
    }
}
