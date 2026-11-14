using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CodeSwitchX.Core.Paths;
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

    /// <summary>Windows' "another program has the file open" and "has part of it locked": a refusal for a moment, not for good.</summary>
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// The scripts of the repository whose top is <paramref name="top"/> (git found it, <see cref="Core.Workspaces.GitInfo.Top"/>); null
    /// when it has none. A lookup that goes wrong in any way must not cost the tile its git round. A read another program holds up for a
    /// moment (an editor or a scanner has the file open) says nothing new: it gives what the last round found there (#425).
    /// </summary>
    /// <param name="known">The scripts the tile's lines had after the last round: what a held file keeps.</param>
    public static StableBuildRepo? Find(string top, IEnumerable<StableBuildRepo?>? known = null)
    {
        try
        {
            var scripts = Path.Combine(top, "scripts");
            var common = Path.Combine(scripts, "_common.ps1");
            if (!File.Exists(Path.Combine(scripts, "build.ps1")) || !File.Exists(common))
            {
                return null;
            }

            var text = WithoutComments(File.ReadAllText(common));
            if (Setting(text, "ShortcutName") is not { } shortcut
                || shortcut != Path.GetFileName(shortcut) || !shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // Only a bare shortcut name: Run opens what it names, and must not open a program from elsewhere.
                return null;
            }

            return new StableBuildRepo(top, shortcut, Setting(text, "ReleaseBranch"));
        }
        catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation)
        {
            // What another repository had says nothing about this one. A file that is gone, may not be read, or sits on a share that
            // went away has no scripts to offer.
            var key = PathNormalizer.Normalize(top);
            return known?.FirstOrDefault(s => s is not null && PathNormalizer.Normalize(s.RepoPath) == key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file's code without its comments (#425): a <c>#</c> line comment and a <c>&lt;# … #&gt;</c> block (they do not nest) give way,
    /// so an assignment inside one is no setting. Quoted strings and here-strings are stepped over, so a <c>#</c> or <c>&lt;#</c> inside
    /// one starts no comment, and neither does a <c>#</c> within a word or escaped with a backtick, as in PowerShell. Line breaks stay,
    /// so a setting after a comment still starts its line.
    /// </summary>
    internal static string WithoutComments(string text)
    {
        var code = new StringBuilder(text.Length);
        var inBlock = false;
        var quote = '\0';
        var hereString = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (hereString != '\0')
            {
                code.Append(c);
                // It ends with its quote and @ at the start of a line.
                if (c == '\n' && i + 2 < text.Length && text[i + 1] == hereString && text[i + 2] == '@')
                {
                    code.Append(hereString).Append('@');
                    i += 2;
                    hereString = '\0';
                }
            }
            else if (inBlock)
            {
                if (c == '#' && next == '>')
                {
                    inBlock = false;
                    i++;
                }
                else if (c == '\n')
                {
                    code.Append(c);
                }
            }
            else if (quote != '\0')
            {
                code.Append(c);
                if (quote == '"' && c == '`' && next != '\0')
                {
                    code.Append(next); // an escaped character, a quote among them, ends nothing
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0'; // a doubled quote closes and opens again
                }
            }
            else if (c == '@' && next is '\'' or '"' && LineEndsAfter(text, i + 2))
            {
                hereString = next;
                code.Append(c).Append(next);
                i++;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                code.Append(c);
            }
            else if (c == '`' && next != '\0')
            {
                code.Append(c).Append(next); // an escaped character is no quote and no comment
                i++;
            }
            else if (c == '<' && next == '#')
            {
                inBlock = true;
                i++;
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is ';' or '(' or '{' or '|' or ','))
            {
                while (i + 1 < text.Length && text[i + 1] != '\n')
                {
                    i++;
                }
            }
            else
            {
                code.Append(c);
            }
        }

        return code.ToString();

        // A here-string's opening is the last thing on its line, but for spaces.
        static bool LineEndsAfter(string text, int at)
        {
            for (; at < text.Length && text[at] != '\n'; at++)
            {
                if (!char.IsWhiteSpace(text[at]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The setting's value as the file writes it; null when it is not there, or is a double-quoted string PowerShell would
    /// fill in (a $ or a backtick in it): its text is then not the value the script uses.
    /// </summary>
    private static string? Setting(string text, string name)
    {
        // The last assignment, as it is the one the script ends up with.
        if (SettingLine().Matches(text).LastOrDefault(m => string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase)) is not { } match)
        {
            return null;
        }

        if (match.Groups["single"].Success)
        {
            return match.Groups["single"].Value.Replace("''", "'") is { Length: > 0 } single ? single : null;
        }

        var value = match.Groups["double"].Value;
        return value.Length > 0 && value.IndexOfAny(['$', '`']) < 0 ? value : null;
    }

    /// <summary><c>$script:Name = 'value'</c> or <c>"value"</c> at a line's start; PowerShell takes the scope in any casing.</summary>
    [GeneratedRegex("""^\s*\$script:(?<name>\w+)\s*=\s*(?:'(?<single>(?:[^']|'')*)'|"(?<double>[^"]*)")""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
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
            if (!string.Equals(line.Branch, release, StringComparison.OrdinalIgnoreCase)) // as the script's -ne compares
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
            // The script fetches and refuses a branch that is behind (#426): said here, before a console opens to say it.
            0 => line.Behind switch
            {
                null => null,
                { Commits: null } => "Behind GitHub - pull first",
                { Commits: var commits } => $"{GitLine.CommitsText(commits)} behind GitHub - pull first",
            },
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
        // -w new: a window of its own, not a tab in one the user types in, whatever Windows Terminal is set to do.
        return $"-w new -d \"{quoted.Replace(";", "\\;")}\" {shell} {ShellArguments(repo.BuildScript.Replace(";", "\\;"))}";
    }

    private static string ShellArguments(string script) => $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{script}\"";

    public bool Run(string shortcutPath) => ShellStart.TryStart(new ProcessStartInfo(shortcutPath));

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(dir => File.Exists(Path.Combine(dir, exe)));
}

/// <summary>Whether a <see cref="StableBuildEntry"/> has a Run, and whether it names the app (#425).</summary>
public enum StableBuildRun
{
    /// <summary>"Run": the only shortcut on the tile.</summary>
    Plain,

    /// <summary>"Run Name": the tile's entries open several shortcuts.</summary>
    Named,

    /// <summary>No Run: an entry before it opens the same shortcut (a checkout and a worktree of one app).</summary>
    None,
}

/// <summary>One repository's entries on a tile's menu (#423), made when the menu opens: Build, and Run unless another repository on the tile opens the same shortcut (#425).</summary>
public sealed partial class StableBuildEntry
{
    private readonly StableBuildRepo _repo;
    private readonly string _shortcutPath;
    private readonly IStableBuildLauncher _launcher;
    private readonly IYardDialogs _dialogs;
    private readonly GitLine _line;
    private readonly Func<GitLine, Task<GitLine?>>? _freshLine;

    /// <param name="line">A line with stable-build scripts; its git state is what the precheck reads.</param>
    /// <param name="named">The tile has several such repositories: Build says which.</param>
    /// <param name="run">The entry's Run (#425).</param>
    /// <param name="freshLine">The line as git has it now; null when it could not be read. Build checks it before it asks (#425).</param>
    public StableBuildEntry(GitLine line, bool named, StableBuildRun run, IStableBuildLauncher launcher, IYardDialogs dialogs, Func<GitLine, Task<GitLine?>>? freshLine = null)
    {
        var repo = line.StableBuild ?? throw new ArgumentException("The line has no stable-build scripts.", nameof(line));
        _repo = repo;
        _line = line;
        _launcher = launcher;
        _dialogs = dialogs;
        _freshLine = freshLine;
        var folder = Path.TrimEndingDirectorySeparator(repo.RepoPath);
        // The name the tile's line shows (a workspace file's label), else the folder's; a drive root has no folder name: "E:\".
        Name = line.Folder ?? (Path.GetFileName(folder) is { Length: > 0 } name ? name : folder);
        BuildHeader = named ? $"Build {Name}" : "Build new version";
        RunHeader = run switch
        {
            StableBuildRun.Plain => "Run",
            StableBuildRun.Named => $"Run {Name}",
            _ => null,
        };
        BuildBlock = StableBuildCheck.BuildBlock(line);
        _shortcutPath = launcher.ShortcutPath(repo.ShortcutName);
        RunBlock = launcher.ShortcutExists(_shortcutPath) ? null : "No stable build yet";
    }

    /// <summary>The repository's folder name: what the dialog and the headers call the app.</summary>
    public string Name { get; }
    public string BuildHeader { get; }

    /// <summary>Null when the entry has no Run: another one on the tile opens the same shortcut (#425).</summary>
    public string? RunHeader { get; }
    public string? BuildBlock { get; }
    public string? RunBlock { get; }
    public bool CanBuild => BuildBlock is null;
    public bool CanRun => RunHeader is not null && RunBlock is null;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        // The menu's lines can be older than the press (a branch switched a moment ago, #425): the tile's git is read once more, and a
        // block found now is said instead of asking. A read that finds nothing keeps the menu's word; the script checks again anyway.
        if (await FreshBlockAsync().ConfigureAwait(true) is { } block)
        {
            _dialogs.Tell($"Not building {Name}: {block}.");
            return;
        }

        if (!_dialogs.Confirm($"Build a new version of {Name}?\n\nThe build script bumps the version, commits and pushes it, and may close the running {Name} and start the new one. Its window shows every step."))
        {
            return;
        }

        // Off the UI thread: the launcher looks along PATH, and a folder there on an offline share blocks for about 20 s.
        if (!await Task.Run(() => _launcher.StartBuild(_repo)).ConfigureAwait(true))
        {
            _dialogs.Tell($"Could not start the build of {Name}.");
        }
    }

    /// <summary>Why the repository cannot be built as it stands now; null when it can, or a fresh read found nothing to say.</summary>
    private async Task<string?> FreshBlockAsync()
    {
        if (_freshLine is null || await _freshLine(_line).ConfigureAwait(true) is not { } line)
        {
            return null;
        }

        return line switch
        {
            // Not a repository right now (a share gone offline, git gave no state): nothing new either.
            { IsRepository: false } or { Branch: null } or { DirtyCount: null } => null,
            { StableBuild: null } => "its build scripts are not there any more",
            _ => StableBuildCheck.BuildBlock(line),
        };
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!await Task.Run(() => _launcher.Run(_shortcutPath)).ConfigureAwait(true))
        {
            _dialogs.Tell($"Could not start {Name}.");
        }
    }
}
