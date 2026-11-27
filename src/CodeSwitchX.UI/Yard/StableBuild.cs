using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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
/// <param name="MarkerPath">
/// The install marker the build writes into the current version's folder (#452): <c>&lt;DefaultInstallRoot&gt;\&lt;AppName&gt;\&lt;InstallMarkerName&gt;</c>;
/// null when the file does not name all three plainly.
/// </param>
public sealed partial record StableBuildRepo(string RepoPath, string ShortcutName, string? ReleaseBranch = null, string? MarkerPath = null)
{
    public string BuildScript => Path.Combine(RepoPath, "scripts", "build.ps1");

    /// <summary>Windows' "another program has the file open" and "has part of it locked": a refusal for a moment, not for good.</summary>
    internal const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

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

            return new StableBuildRepo(top, shortcut, Setting(text, "ReleaseBranch"), MarkerPathOf(text));
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
    /// Where the marker is (#452), from the three settings the scripts make its path of. Only a root that names a drive or a share, a bare
    /// app name and a bare .json file name: the path must not depend on where the app was started, nor lead out of the install folder.
    /// The marker's name is read, not made of the app's: a renamed app may keep its old marker name.
    /// </summary>
    private static string? MarkerPathOf(string text)
    {
        if (Setting(text, "DefaultInstallRoot") is not { } root || !Path.IsPathFullyQualified(root)
            || Setting(text, "AppName") is not { } app || app != Path.GetFileName(app)
            || Setting(text, "InstallMarkerName") is not { } marker || marker != Path.GetFileName(marker)
            || !marker.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(root, app, marker);
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

/// <summary>The stable build installed now (#452), as its marker says.</summary>
/// <param name="Version">The version the build gave the app.</param>
/// <param name="Commit">The (short) hash the build was made from; null when git was not there at build time, or the marker holds no hash.</param>
public sealed partial record StableBuildInstall(string Version, string? Commit)
{
    /// <summary>
    /// What the marker at <paramref name="markerPath"/> says. A marker that is not there, says it is not <c>complete</c> (the build stopped
    /// half way) or names no version is no build; one with no <c>complete</c> at all comes from an older build script, which writes it only
    /// once the build is done. One another program holds for a moment (a scanner), or that is empty or torn (the build writing it this very
    /// moment), says nothing new: unknown.
    /// </summary>
    public static StableBuildRead Read(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return StableBuildRead.NoBuild;
            }

            string text;
            // Shared for writing and deleting: the build script must never fail to write it because the Yard reads it. The reader drops
            // the byte order mark PowerShell 5.1 writes, which JsonDocument would not take.
            using (var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = reader.ReadToEnd();
            }

            using var json = JsonDocument.Parse(text);
            var marker = json.RootElement;
            if (marker.ValueKind != JsonValueKind.Object
                || (marker.TryGetProperty("complete", out var complete) && complete.ValueKind != JsonValueKind.True)
                || !marker.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || version.GetString() is not { Length: > 0 } installed)
            {
                return StableBuildRead.NoBuild;
            }

            // The commit goes onto git's command line later: only a hash is taken.
            var commit = marker.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { } hash && ShortHash().IsMatch(hash) ? hash : null;
            return new StableBuildRead(true, new StableBuildInstall(installed, commit));
        }
        catch (IOException ex) when (ex.HResult is StableBuildRepo.SharingViolation or StableBuildRepo.LockViolation)
        {
            return StableBuildRead.Unknown;
        }
        catch (JsonException)
        {
            // Read while the build script writes it: an empty file, or half of one. What was shown stays.
            return StableBuildRead.Unknown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StableBuildRead.Unknown;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{4,40}$")]
    private static partial Regex ShortHash();
}

/// <summary>What reading a stable build's marker found (#452).</summary>
/// <param name="Known">The marker could be read, or is not there; false while another program holds it.</param>
/// <param name="Install">The build it names; null for no build, or when not known.</param>
public readonly record struct StableBuildRead(bool Known, StableBuildInstall? Install)
{
    public static readonly StableBuildRead Unknown = new(false, null);
    public static readonly StableBuildRead NoBuild = new(true, null);
}

/// <summary>Where the installed stable build stands against GitHub's default branch (#452).</summary>
public enum StableFreshness
{
    /// <summary>The repository has the scripts, and no build is installed (no marker, or one the build did not finish).</summary>
    NoBuild,

    /// <summary>Not compared: GitHub not read, no commit in the marker, or git could not tell.</summary>
    Unknown,

    /// <summary>The default branch has nothing after the build's commit but the build script's bump commits.</summary>
    UpToDate,

    /// <summary>The default branch has commits the build was not made from.</summary>
    Outdated,
}

/// <summary>A line's stable build (#452): its version, and whether GitHub's default branch moved on since it was made.</summary>
/// <param name="Version">The installed version; null for no build.</param>
/// <param name="NewerCommits">How many commits the default branch has since the build; null when not known.</param>
/// <param name="Branch">GitHub's default branch the build was compared with.</param>
/// <param name="BuiltFrom">The commit the build was made from, as its marker has it; null when it names none.</param>
/// <param name="ComparedWith">GitHub's head, the full hash, that git compared the build with; null when it was not compared.</param>
/// <param name="LookedAt">
/// The head of GitHub's default branch the status was worked out against, also when git gave no answer for it; null when none was read.
/// Another head on GitHub now is what makes git be asked again (<see cref="GitLine.StableIsOutOfDate"/>).
/// </param>
public sealed record StableBuildStatus(StableFreshness Freshness, string? Version = null, int? NewerCommits = null, string? Branch = null, string? BuiltFrom = null, string? ComparedWith = null,
    string? LookedAt = null)
{
    /// <summary>The pill's word; null where nothing is known, so no pill rather than a guess.</summary>
    public string? Label => Freshness switch
    {
        StableFreshness.UpToDate => "up to date",
        StableFreshness.Outdated => "outdated",
        StableFreshness.NoBuild => "no stable build",
        _ => null,
    };

    /// <summary>"Stable 0.1.0.45"; null without a version.</summary>
    public string? VersionText => Version is null ? null : $"Stable {Version}";

    public string ToolTip => Freshness switch
    {
        StableFreshness.UpToDate => $"{Version} was built from {Branch} as it is on GitHub now.",
        StableFreshness.Outdated when NewerCommits is { } n => $"{GitLine.CommitsText(n)} on {Branch} since {Version}.",
        StableFreshness.Outdated => $"{Branch} on GitHub has commits newer than {Version}.",
        StableFreshness.NoBuild => "No stable build installed yet.",
        _ => $"{Version} - not compared with GitHub yet.",
    };
}

/// <summary>
/// One installed app's row under a tile's chats (#452): its version, its Run and Build buttons (#454) and the pill. A click on the row
/// outside the buttons goes to the tile.
/// </summary>
/// <param name="Title">"Stable 0.1.0.45", with the app's name before it on a tile with several ("App · Stable 0.1.0.45"); the name alone, or null on a lone row, for no build.</param>
/// <param name="Buttons">The buttons' state as of the tile's git round (#454); null when the row's repository has no entry.</param>
/// <param name="Lead">
/// The dimmed text before the number (#454): "Stable", "App · Stable" on a tile with several, the app's name alone for no build there,
/// null on a lone row without a version.
/// </param>
public sealed record StableBuildRow(string? Title, StableBuildStatus Status, StableBuildButtons? Buttons = null, string? Lead = null)
{
    internal static string? TitleOf(StableBuildStatus status, string? name) => (name, status.VersionText) switch
    {
        ({ } app, { } version) => $"{app} · {version}",
        var (app, version) => app ?? version,
    };

    internal static string? LeadOf(StableBuildStatus status, string? name) => (name, status.Version) switch
    {
        ({ } app, not null) => $"{app} · Stable",
        ({ } app, null) => app,
        (null, not null) => "Stable",
        _ => null,
    };
}

/// <summary>
/// What a stable row's Run and Build buttons show (#454), taken from the tile's entry for the row's repository. Plain values, so an
/// unchanged round gives an equal row and the list stays (an open tooltip with it); the tile's commands make the entry afresh at the press.
/// </summary>
/// <param name="RepoPath">The entry's repository: what the tile's commands find the entry by at the press.</param>
/// <param name="Name">What the dialogs call the app.</param>
/// <param name="BuildName">The Build entry's header: "Build new version", or "Build App" on a tile with several.</param>
/// <param name="BuildBlock">Why Build is greyed; null when it may start.</param>
/// <param name="RunName">The Run entry's header; null for no Run (another repository on the tile opens the same shortcut).</param>
/// <param name="RunBlock">Why Run is greyed; null when the shortcut is there.</param>
public sealed record StableBuildButtons(string RepoPath, string Name, string BuildName, string? BuildBlock, string? RunName, string? RunBlock)
{
    public bool CanBuild => BuildBlock is null;
    public bool CanRun => RunName is not null && RunBlock is null;
    public bool HasRun => RunName is not null;
    public string BuildToolTip => BuildBlock ?? BuildName;
    public string? RunToolTip => RunBlock ?? RunName;

    public static StableBuildButtons Of(StableBuildEntry entry) =>
        new(entry.RepoPath, entry.Name, entry.BuildHeader, entry.BuildBlock, entry.RunHeader, entry.RunBlock);
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
        Name = NameOf(line);
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

    /// <summary>
    /// What the entries call the app of a line with stable-build scripts, and so does its stable build's row (#452): the name the tile's
    /// line shows (a workspace file's label), else the folder's; a drive root has no folder name: "E:\".
    /// </summary>
    internal static string NameOf(GitLine line)
    {
        var folder = Path.TrimEndingDirectorySeparator(line.StableBuild?.RepoPath ?? line.Path ?? string.Empty);
        return line.Folder ?? (Path.GetFileName(folder) is { Length: > 0 } name ? name : folder);
    }

    /// <summary>The repository's folder name: what the dialog and the headers call the app.</summary>
    public string Name { get; }

    /// <summary>The repository the entry builds: what a stable row's buttons find their entry by (#454).</summary>
    internal string RepoPath => _repo.RepoPath;
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
