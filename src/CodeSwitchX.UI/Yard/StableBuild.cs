using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using Path = System.IO.Path;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// A repository that carries the stable-build scripts (#423): <c>scripts\build.ps1</c> next to a <c>scripts\_common.ps1</c>
/// that names the Start Menu shortcut the build writes. The tile's menu builds it and starts it.
/// </summary>
/// <param name="RepoPath">The repository's folder, as its git line has it.</param>
/// <param name="ShortcutName">The shortcut's file name in the Start Menu's Programs folder, e.g. "Some App.lnk".</param>
public sealed partial record StableBuildRepo(string RepoPath, string ShortcutName)
{
    public string BuildScript => Path.Combine(RepoPath, "scripts", "build.ps1");

    /// <summary>The repository's scripts, or null when it has none, or they cannot be read.</summary>
    public static StableBuildRepo? Find(string repoPath)
    {
        try
        {
            var scripts = Path.Combine(repoPath, "scripts");
            var common = Path.Combine(scripts, "_common.ps1");
            if (!File.Exists(Path.Combine(scripts, "build.ps1")) || !File.Exists(common))
            {
                return null;
            }

            return ShortcutLine().Match(File.ReadAllText(common)) is { Success: true } match
                ? new StableBuildRepo(repoPath, match.Groups[1].Value)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The round's lines, each with its repository's scripts, looked for afresh: they come and go with a branch. A line the
    /// round carried over from <paramref name="previous"/> (the same instance) keeps what it had: its folder could not be
    /// checked, and on an offline share every look blocks for about 20 s, one line after another.
    /// </summary>
    internal static List<GitLine> Mark(IReadOnlyList<GitLine> lines, IReadOnlyList<GitLine> previous, Func<string, StableBuildRepo?> find) =>
        [.. lines.Select(l => previous.Any(p => ReferenceEquals(p, l))
            ? l
            : l with { StableBuild = l.IsRepository && l.Path is { } path ? find(path) : null })];

    [GeneratedRegex("""^\s*\$script:ShortcutName\s*=\s*['"]([^'"]+)['"]""", RegexOptions.Multiline)]
    private static partial Regex ShortcutLine();
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

        if (line.Branch is not ("main" or "develop"))
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
        var folder = repo.RepoPath.TrimEnd('\\', '/');
        var shell = OnPath("pwsh.exe") ? "pwsh.exe" : "powershell.exe";
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{repo.BuildScript}\"";
        return TryStart(new ProcessStartInfo("wt.exe", $"-d \"{folder}\" {shell} {arguments}") { UseShellExecute = true })
            || TryStart(new ProcessStartInfo(shell, arguments) { UseShellExecute = true, WorkingDirectory = folder });
    }

    public bool Run(string shortcutPath) => TryStart(new ProcessStartInfo(shortcutPath) { UseShellExecute = true });

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(dir => File.Exists(Path.Combine(dir, exe)));

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

/// <summary>One repository's pair of entries on a tile's menu (#423), made when the menu opens.</summary>
public sealed partial class StableBuildEntry
{
    private readonly StableBuildRepo _repo;
    private readonly string _shortcutPath;
    private readonly IStableBuildLauncher _launcher;
    private readonly IYardDialogs _dialogs;

    /// <param name="named">The tile has several such repositories: the entries say which.</param>
    public StableBuildEntry(StableBuildRepo repo, GitLine line, bool named, IStableBuildLauncher launcher, IYardDialogs dialogs)
    {
        _repo = repo;
        _launcher = launcher;
        _dialogs = dialogs;
        Name = Path.GetFileName(repo.RepoPath.TrimEnd('\\', '/'));
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

    [RelayCommand]
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

    [RelayCommand]
    private void Run()
    {
        if (!_launcher.Run(_shortcutPath))
        {
            _dialogs.Tell($"Could not start {Name}.");
        }
    }
}
