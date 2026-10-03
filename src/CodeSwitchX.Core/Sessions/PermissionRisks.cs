using System.Text;
using System.Text.RegularExpressions;

namespace CodeSwitchX.Core.Sessions;

/// <summary>What is risky in what a chat asks permission for, named whatever else is said of it.</summary>
public enum PermissionRisk
{
    /// <summary><c>rm</c>, <c>del</c>, <c>Remove-Item</c>, <c>rmdir</c>, <c>git clean</c>, <c>git rm</c>, <c>find -delete</c>.</summary>
    DeletesFiles,

    /// <summary>A Write of nothing over a file that is there.</summary>
    EmptiesAFile,

    /// <summary><c>git push</c>.</summary>
    Pushes,

    /// <summary>A forced push, a rebase, an amended commit, <c>git filter-branch</c>.</summary>
    RewritesHistory,

    /// <summary><c>git reset --hard</c>, <c>git checkout -- &lt;path&gt;</c>, <c>git restore</c>, <c>git stash drop</c>.</summary>
    DiscardsChanges,

    /// <summary>An edit, a write, a copy or a shell redirect to a path that is not under the chat's folder.</summary>
    WritesOutsideItsFolder,
}

/// <summary>The shell a command is written for: how it quotes and escapes.</summary>
public enum ShellDialect
{
    /// <summary>Bash and the like: <c>\</c> escapes, <c>$( )</c> and backticks run commands, here-documents.</summary>
    Posix,

    /// <summary>PowerShell: a backtick escapes, <c>$( )</c> runs a command, here-strings.</summary>
    PowerShell,

    /// <summary>cmd.exe: <c>^</c> escapes.</summary>
    Cmd,
}

/// <summary>
/// Finds what is risky in a command or a write, by rules rather than by a model, so what Raven says of a permission prompt
/// cannot leave it out. The rules read the command as a shell would, roughly: the command at the start of each part
/// (after <c>;</c>, <c>&amp;&amp;</c>, <c>|</c>, a bracket or a newline), past <c>sudo</c>, <c>xargs</c>, <c>timeout</c> and
/// their options; what <c>bash -c</c>, <c>pwsh -Command</c>, <c>eval</c>, <c>iex</c> or <c>$( )</c> runs, quoted or not;
/// where <c>&gt;</c> writes, after any <c>cd</c> the command makes. Where the command cannot be read for sure (a quote
/// left open), they err on the side of naming.
/// </summary>
public static class PermissionRisks
{
    private static readonly HashSet<string> Deleters = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "rd", "del", "erase", "unlink", "shred", "remove-item", "ri",
    };

    /// <summary>Commands that run the rest of their words as a command, and their options that take a value.</summary>
    private static readonly Dictionary<string, string[]> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sudo"] = ["-u", "-g", "-C", "-D", "-h", "-p", "-r", "-t", "-U", "-T", "--user", "--group", "--chdir", "--host", "--prompt"],
        ["doas"] = ["-u", "-C"],
        ["nohup"] = [],
        ["time"] = [],
        ["command"] = [],
        ["builtin"] = [],
        ["exec"] = ["-a"],
        ["env"] = ["-u", "-C", "--unset", "--chdir"],
        ["xargs"] = ["-n", "-L", "-P", "-I", "-d", "-E", "-s", "-a", "--max-args", "--max-procs", "--delimiter", "--arg-file"],
        ["nice"] = ["-n", "--adjustment"],
        ["timeout"] = ["-s", "-k", "--signal", "--kill-after"],
        ["stdbuf"] = ["-i", "-o", "-e"],
    };

    /// <summary>Runners whose first word that is no option is theirs too: <c>timeout 30 rm …</c>.</summary>
    private static readonly HashSet<string> RunnersWithAnArgument = new(StringComparer.OrdinalIgnoreCase) { "timeout" };

    private static readonly HashSet<string> PosixShells = new(StringComparer.OrdinalIgnoreCase) { "bash", "sh", "zsh", "dash", "ksh" };

    /// <summary>pwsh's and Windows PowerShell's options that take a value, by name and alias.</summary>
    private static readonly (string Name, string[] Aliases)[] PowerShellValued =
    [
        ("-executionpolicy", ["-ep", "-ex"]),
        ("-windowstyle", ["-w"]),
        ("-workingdirectory", ["-wd"]),
        ("-configurationname", ["-config"]),
        ("-configurationfile", []),
        ("-inputformat", ["-inp", "-if"]),
        ("-outputformat", ["-o", "-of"]),
        ("-psconsolefile", []),
        ("-version", ["-v"]),
        ("-settingsfile", ["-settings"]),
        ("-custompipename", []),
    ];

    /// <summary>PowerShell's writers, and <c>tee</c>: each writes to its path.</summary>
    private static readonly HashSet<string> Writers = new(StringComparer.OrdinalIgnoreCase)
    {
        "out-file", "set-content", "add-content", "tee", "tee-object",
    };

    /// <summary>Copies and moves: each writes to its last path.</summary>
    private static readonly HashSet<string> Copiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cp", "mv", "copy", "move", "copy-item", "move-item", "cpi", "mi",
    };

    private static readonly HashSet<string> Movers = new(StringComparer.OrdinalIgnoreCase) { "cd", "chdir", "set-location", "sl" };

    /// <summary>Where a redirect writes to no file.</summary>
    private static readonly HashSet<string> Nowhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "/dev/null", "/dev/stdout", "/dev/stderr", "/dev/tty", "nul", "nul:", "$null", "con",
    };

    /// <summary>A file's extension at the end of a word: ".cs", ".json"; not "v1.2".</summary>
    private static readonly Regex FileExtension = new(@"\.[A-Za-z][A-Za-z0-9]{0,5}$", RegexOptions.CultureInvariant);

    /// <summary>A here-document's start: <c>&lt;&lt;EOF</c>, <c>&lt;&lt;-'END'</c>; not a here-string (<c>&lt;&lt;&lt;</c>).</summary>
    private static readonly Regex HereDocument = new(@"\G<<(?!<)(?<dash>-)?\s*(?<quote>['""]?)(?<word>[A-Za-z_][A-Za-z0-9_]*)\k<quote>", RegexOptions.CultureInvariant);

    /// <summary>What is risky in a shell command, in the enum's order.</summary>
    /// <param name="folder">The chat's project folder; null when it is not known, and then nothing is named as outside it.</param>
    /// <param name="cwd">Where the command starts; the project folder when null.</param>
    public static IReadOnlyList<PermissionRisk> OfCommand(string command, string? folder, string? cwd = null, ShellDialect dialect = ShellDialect.Posix)
    {
        var scan = new Scanner(folder is { Length: > 0 } ? folder : null, cwd is { Length: > 0 } ? cwd : folder);
        scan.Command(command, dialect, depth: 0);
        return Ordered(scan.Risks);
    }

    /// <summary>What is risky in an edit or a write of <paramref name="path"/>.</summary>
    /// <param name="emptiesIt">The write puts nothing over a file that is there.</param>
    /// <param name="cwd">What a relative path is under; the project folder when null.</param>
    public static IReadOnlyList<PermissionRisk> OfWrite(string path, string? folder, bool emptiesIt = false, string? cwd = null)
    {
        var scan = new Scanner(folder is { Length: > 0 } ? folder : null, cwd is { Length: > 0 } ? cwd : folder);
        if (emptiesIt)
        {
            scan.Risks.Add(PermissionRisk.EmptiesAFile);
        }

        scan.WritesTo(path);
        return Ordered(scan.Risks);
    }

    /// <summary>The risk as Raven says it after "It ": "deletes files".</summary>
    public static string Says(PermissionRisk risk) => risk switch
    {
        PermissionRisk.DeletesFiles => "deletes files",
        PermissionRisk.EmptiesAFile => "empties a file",
        PermissionRisk.Pushes => "pushes to a remote",
        PermissionRisk.RewritesHistory => "rewrites history",
        PermissionRisk.DiscardsChanges => "throws away uncommitted changes",
        PermissionRisk.WritesOutsideItsFolder => "writes outside its folder",
        _ => risk.ToString(),
    };

    /// <summary>"deletes files", "deletes files and pushes to a remote", "deletes files, pushes to a remote and rewrites history".</summary>
    public static string Phrase(IReadOnlyList<PermissionRisk> risks)
    {
        var said = risks.Select(Says).ToList();
        return said.Count <= 1 ? string.Concat(said) : string.Join(", ", said[..^1]) + " and " + said[^1];
    }

    /// <summary>
    /// Whether <paramref name="path"/> is under <paramref name="folder"/>: true or false where that is known, null where it
    /// is not (a variable that is not set, a relative path with no known start, a path no file system takes). A relative
    /// path is under <paramref name="cwd"/> (the folder itself when null); <c>~</c>, <c>$HOME</c>, <c>$env:NAME</c>,
    /// <c>%NAME%</c> and Git Bash's <c>/c/…</c> are read as the machine has them.
    /// </summary>
    public static bool? IsInside(string path, string folder, string? cwd = null)
    {
        if (Full(path, cwd ?? folder) is not { } full)
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.TrimEndingDirectorySeparator(full), root, comparison)
                || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PermissionRisk> Ordered(HashSet<PermissionRisk> risks) => risks.Order().ToList();

    /// <summary>The full path, a relative one under <paramref name="from"/>; null where that cannot be told.</summary>
    private static string? Full(string path, string? from)
    {
        if (Expanded(path.Trim()) is not { Length: > 0 } expanded)
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            return from is null || Full(from, null) is not { } start ? null : Path.GetFullPath(Path.Combine(start, expanded));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The path with its home and variables put in, a Git Bash path as Windows has it; null where a variable is not set.</summary>
    private static string? Expanded(string path) => Variables(path) is { } expanded ? GitBash(expanded) : null;

    /// <summary>The path with its home and variables put in; null where one of them is not set.</summary>
    private static string? Variables(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        }

        if (path.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
        {
            var end = path.IndexOfAny(['/', '\\'], 5);
            var name = end < 0 ? path[5..] : path[5..end];
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value + (end < 0 ? "" : path[end..]) : null;
        }

        if (path.StartsWith('$'))
        {
            var braced = path.StartsWith("${", StringComparison.Ordinal);
            var start = braced ? 2 : 1;
            var end = braced ? path.IndexOf('}', start) : path.IndexOfAny(['/', '\\'], start);
            var name = end < 0 ? path[start..] : path[start..end];
            name = braced && name.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? name[4..] : name; // PowerShell's ${env:TEMP}
            var rest = end < 0 ? "" : path[(braced ? end + 1 : end)..];
            var value = name == "HOME" ? Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Environment.GetEnvironmentVariable(name);
            return value is { Length: > 0 } ? value + rest : null;
        }

        if (path.Contains('%'))
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return expanded.Contains('%') ? null : expanded;
        }

        return path;
    }

    /// <summary>Git Bash's /c/Users/… is C:\Users\…; on Windows only.</summary>
    private static string GitBash(string path) =>
        OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == '/'
            ? $"{char.ToUpperInvariant(path[1])}:\\{path[3..]}"
            : path;

    /// <summary>A command read part by part, with where it is (a <c>cd</c> moves it) and the risks found so far.</summary>
    private sealed class Scanner(string? folder, string? here)
    {
        public HashSet<PermissionRisk> Risks { get; } = [];

        /// <summary>Where the command is now; null after a <c>cd</c> to where it cannot be told.</summary>
        private string? _here = here;

        /// <summary>Where each <c>pushd</c> was, for its <c>popd</c>.</summary>
        private readonly Stack<string?> _pushed = new();

        public void WritesTo(string path)
        {
            // After a cd to where it cannot be told, a relative path is nowhere known: it is not named.
            if (folder is not null && !Nowhere.Contains(path.Trim()) && !path.StartsWith("/dev/fd/", StringComparison.Ordinal)
                && Full(path, _here) is { } full && IsInside(full, folder) == false)
            {
                Risks.Add(PermissionRisk.WritesOutsideItsFolder);
            }
        }

        public void Command(string command, ShellDialect dialect, int depth)
        {
            if (depth > 4)
            {
                return;
            }

            var (tokens, inner) = Tokens(dialect == ShellDialect.Posix ? WithoutHereDocuments(command) : command, dialect);
            var part = new List<string>();
            List<string>? piped = null; // the part before a "|": what it writes, the next part may run
            var subshells = new Stack<string?>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Redirect)
                {
                    if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Word)
                    {
                        WritesTo(tokens[++i].Text);
                    }

                    continue;
                }

                if (token.Kind == TokenKind.Separator)
                {
                    Simple(part, dialect, depth, piped);
                    piped = token.Text == "|" ? [.. part] : null;
                    part.Clear();
                    // A cd in ( … ) or $( … ) is the subshell's own; in PowerShell and cmd brackets only group.
                    if (dialect == ShellDialect.Posix && token.Text == "(")
                    {
                        subshells.Push(_here);
                    }
                    else if (dialect == ShellDialect.Posix && token.Text == ")" && subshells.Count > 0)
                    {
                        _here = subshells.Pop();
                    }

                    continue;
                }

                part.Add(token.Text);
            }

            Simple(part, dialect, depth, piped);
            foreach (var run in inner)
            {
                Command(run, dialect, depth + 1); // what $( ) or backticks run inside a quoted word
            }
        }

        /// <summary>
        /// One simple command: its name and its words. <paramref name="piped"/> is the part whose output it reads, run as
        /// commands by a shell that reads them (<c>echo "git reset --hard" | bash</c>, <c>@' … '@ | iex</c>).
        /// </summary>
        private void Simple(List<string> words, ShellDialect dialect, int depth, List<string>? piped = null)
        {
            if (Named(words) is not { } named)
            {
                return;
            }

            var (name, args) = named;

            if (piped is { Count: > 0 } && RunsItsInput(name, args) is { } runs && Written(piped) is { } written)
            {
                Child(written, runs, depth);
            }

            Simple(name, args, dialect, depth);
        }

        /// <summary>The command's name and its words, past assignments, runners and their options; null when there is none.</summary>
        private static (string Name, List<string> Args)? Named(List<string> words)
        {
            var at = 0;
            // PowerShell's "$out = git push": the command is what is assigned.
            if (words.Count > 0 && words[0].StartsWith('$'))
            {
                if (words.Count > 1 && words[1] == "=")
                {
                    at = 2;
                }
                else if (words[0].EndsWith('='))
                {
                    at = 1;
                }
                else if (words[0].IndexOf('=') is var equals and > 0 && equals < words[0].Length - 1)
                {
                    words = [words[0][(equals + 1)..], .. words.Skip(1)];
                }
            }

            while (at < words.Count)
            {
                if (IsAssignment(words[at]))
                {
                    at++;
                    continue;
                }

                if (!Runners.TryGetValue(Name(words[at]), out var valued))
                {
                    break;
                }

                var runner = Name(words[at]);
                at++;
                while (at < words.Count && (words[at].StartsWith('-') || IsAssignment(words[at])))
                {
                    at += valued.Contains(words[at], StringComparer.Ordinal) ? 2 : 1; // an option's value is no command
                }

                if (RunnersWithAnArgument.Contains(runner))
                {
                    at++; // timeout's duration
                }
            }

            return at >= words.Count ? null : (Name(words[at]), words.Skip(at + 1).ToList());
        }

        /// <summary>The dialect a command reads its input in when it runs it as commands; null when it does not.</summary>
        private static ShellDialect? RunsItsInput(string name, List<string> args)
        {
            if (PosixShells.Contains(name))
            {
                // bash, sh -s, bash -x: commands from what comes in; bash script.sh or bash -c "…" runs something else.
                var runsOther = args.Any(IsRunFlag) || (args.Any(a => !a.StartsWith('-')) && !args.Contains("-s"));
                return runsOther ? null : ShellDialect.Posix;
            }

            return (name.Equals("iex", StringComparison.OrdinalIgnoreCase) || name.Equals("invoke-expression", StringComparison.OrdinalIgnoreCase))
                && args.Count == 0
                ? ShellDialect.PowerShell
                : null;
        }

        /// <summary>What a part writes out, to be read as commands: echo's or Write-Output's words, or a lone string.</summary>
        private static string? Written(List<string> part)
        {
            if (part.Count == 1)
            {
                return part[0];
            }

            return Named(part) is { } named && named.Name.ToLowerInvariant() is "echo" or "printf" or "write-output" or "write" or "write-host"
                ? string.Join(' ', named.Args.Where(a => !a.StartsWith('-')))
                : null;
        }

        /// <summary>bash's -c, and -lc, -ec, -xc: the next word is what it runs.</summary>
        private static bool IsRunFlag(string arg) => arg.Length > 1 && arg[0] == '-' && arg[1] != '-' && arg.Contains('c');

        /// <summary>A command another shell runs: a cd in it is that shell's own.</summary>
        private void Child(string command, ShellDialect dialect, int depth)
        {
            var here = _here;
            Command(command, dialect, depth + 1);
            _here = here;
        }

        private void Simple(string name, List<string> args, ShellDialect dialect, int depth)
        {
            if (Deleters.Contains(name))
            {
                Risks.Add(PermissionRisk.DeletesFiles);
            }
            else if (PosixShells.Contains(name))
            {
                // bash -c "…", bash -lc "…": what it runs is a command of its own.
                var run = args.FindIndex(IsRunFlag);
                if (run >= 0 && run + 1 < args.Count)
                {
                    Child(args[run + 1], ShellDialect.Posix, depth);
                }
            }
            else if (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            {
                PowerShell(name, args, depth);
            }
            else if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                var run = args.FindIndex(a => a.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Equals("/k", StringComparison.OrdinalIgnoreCase));
                if (run >= 0 && run + 1 < args.Count)
                {
                    Child(string.Join(' ', args.Skip(run + 1)), ShellDialect.Cmd, depth);
                }
            }
            else if (name.Equals("eval", StringComparison.OrdinalIgnoreCase))
            {
                Command(string.Join(' ', args), ShellDialect.Posix, depth + 1);
            }
            else if (name.Equals("iex", StringComparison.OrdinalIgnoreCase) || name.Equals("invoke-expression", StringComparison.OrdinalIgnoreCase))
            {
                Command(Value(args, "-command") ?? string.Join(' ', args), ShellDialect.PowerShell, depth + 1);
            }
            else if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
            {
                Git(args);
            }
            else if (name.Equals("find", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Contains("-delete"))
                {
                    Risks.Add(PermissionRisk.DeletesFiles);
                }

                var exec = args.FindIndex(a => a is "-exec" or "-execdir" or "-ok" or "-okdir");
                if (exec >= 0 && depth < 4)
                {
                    Simple(args.Skip(exec + 1).ToList(), dialect, depth + 1);
                }
            }
            else if (Writers.Contains(name))
            {
                foreach (var path in Paths(args, all: name.StartsWith("tee", StringComparison.OrdinalIgnoreCase)))
                {
                    WritesTo(path);
                }
            }
            else if (Copiers.Contains(name))
            {
                var target = Value(args, "-destination") ?? args.LastOrDefault(a => !a.StartsWith('-'));
                if (target is not null && args.Count(a => !a.StartsWith('-')) >= 2)
                {
                    WritesTo(target);
                }
            }
            else if (Movers.Contains(name))
            {
                Move(args, dialect);
            }
            else if (name.Equals("pushd", StringComparison.OrdinalIgnoreCase) || name.Equals("push-location", StringComparison.OrdinalIgnoreCase))
            {
                _pushed.Push(_here);
                Move(args, dialect);
            }
            else if (name.Equals("popd", StringComparison.OrdinalIgnoreCase) || name.Equals("pop-location", StringComparison.OrdinalIgnoreCase))
            {
                _here = _pushed.Count > 0 ? _pushed.Pop() : null;
            }
        }

        /// <summary>
        /// pwsh -Command …, -EncodedCommand … (-e, -ec); Windows PowerShell also runs its first plain word as a command.
        /// An option's value (-ExecutionPolicy Bypass) is no command, and -File runs a script, not a command.
        /// </summary>
        private void PowerShell(string name, List<string> args, int depth)
        {
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (IsPowerShellOption(arg, "-command", "-c") || IsPowerShellOption(arg, "-commandwithargs", "-cwa"))
                {
                    Child(string.Join(' ', args.Skip(i + 1)), ShellDialect.PowerShell, depth);
                    return;
                }

                if (IsPowerShellOption(arg, "-encodedcommand", "-e", "-ec"))
                {
                    try
                    {
                        Child(i + 1 < args.Count ? Encoding.Unicode.GetString(Convert.FromBase64String(args[i + 1])) : "", ShellDialect.PowerShell, depth);
                    }
                    catch (FormatException)
                    {
                        // not base64: nothing to read
                    }

                    return;
                }

                if (IsPowerShellOption(arg, "-file", "-f"))
                {
                    return;
                }

                if (PowerShellValued.Any(o => IsPowerShellOption(arg, o.Name, o.Aliases)))
                {
                    i++; // its value
                    continue;
                }

                if (!arg.StartsWith('-') && name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                {
                    Child(string.Join(' ', args.Skip(i)), ShellDialect.PowerShell, depth);
                    return;
                }
            }
        }

        /// <summary>A <c>cd</c>: where the rest of the command is; unknown where that cannot be told.</summary>
        private void Move(List<string> args, ShellDialect dialect)
        {
            var to = Value(args, "-path") ?? Value(args, "-literalpath")
                ?? args.FirstOrDefault(a => (!a.StartsWith('-') || a == "-") && !(dialect == ShellDialect.Cmd && a.StartsWith('/'))); // cmd's cd /d
            if (to is null)
            {
                if (dialect == ShellDialect.Posix)
                {
                    _here = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); // a bare cd goes home
                }

                return;
            }

            _here = to == "-" ? null : Full(to, _here);
        }

        /// <summary>
        /// "git checkout src/App.cs", "git checkout HEAD~1 src/App.cs": a checkout of paths, which throws away their changes.
        /// One word is a path when it has a file's extension or is a file or folder here; a branch is made with -b, -B or --orphan.
        /// </summary>
        private bool ChecksOutPaths(List<string> rest)
        {
            if (rest.Any(a => a is "-b" or "-B" or "--orphan"))
            {
                return false;
            }

            var plain = rest.Where(a => !a.StartsWith('-')).ToList();
            return plain.Count >= 2 || (plain.Count == 1 && (FileExtension.IsMatch(plain[0])
                || (Full(plain[0], _here) is { } full && (File.Exists(full) || Directory.Exists(full)))));
        }

        private void Git(List<string> args)
        {
            var at = 0;
            while (at < args.Count && args[at].StartsWith('-'))
            {
                at += args[at] is "-C" or "-c" ? 2 : 1; // git's own options: -C <path>, -c <name=value>, --no-pager
            }

            if (at >= args.Count)
            {
                return;
            }

            var verb = args[at].ToLowerInvariant();
            var rest = args.Skip(at + 1).ToList();
            bool Has(params string[] flags) => rest.Any(a => flags.Any(f => a.Equals(f, StringComparison.Ordinal) || a.StartsWith(f + "=", StringComparison.Ordinal)));
            bool HasShort(char flag) => rest.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.Contains(flag));

            switch (verb)
            {
                case "push":
                    Risks.Add(PermissionRisk.Pushes);
                    if (Has("--force", "--force-with-lease", "--force-if-includes", "--mirror") || HasShort('f') || rest.Any(a => a.StartsWith('+')))
                    {
                        Risks.Add(PermissionRisk.RewritesHistory);
                    }

                    break;
                case "reset" when Has("--hard"):
                case "switch" when Has("--force", "--discard-changes") || HasShort('f'):
                // checkout with paths: "git checkout -- src/App.cs", "git checkout .", "git checkout HEAD -- ."
                case "checkout" when Has("--force", "--discard-changes", "--", ".", "--patch") || HasShort('f') || HasShort('p') || ChecksOutPaths(rest):
                case "restore" when !Has("--staged") || Has("--worktree"):
                case "stash" when rest.FirstOrDefault() is "drop" or "clear":
                    Risks.Add(PermissionRisk.DiscardsChanges);
                    break;
                case "rebase" or "filter-branch" or "filter-repo":
                case "commit" when Has("--amend"):
                    Risks.Add(PermissionRisk.RewritesHistory);
                    break;
                case "clean":
                case "rm" when !Has("--cached"):
                    Risks.Add(PermissionRisk.DeletesFiles);
                    break;
            }
        }
    }

    /// <summary>
    /// The command with its here-documents' bodies taken out where they are text (<c>cat &gt; notes.md &lt;&lt;EOF</c>); a
    /// body fed to a shell (<c>bash &lt;&lt;EOF</c>, <c>ssh host &lt;&lt;EOF</c>) is kept, to be read as commands. A
    /// <c>&lt;&lt;</c> inside quotes or <c>$(( ))</c> starts none.
    /// </summary>
    private static string WithoutHereDocuments(string command)
    {
        if (!command.Contains("<<", StringComparison.Ordinal))
        {
            return command;
        }

        var kept = new List<string>();
        var ends = new Queue<(string Word, bool Tabs, bool Runs)>();
        char? quote = null;
        foreach (var line in command.Split('\n'))
        {
            if (ends.Count > 0)
            {
                var (word, tabs, runs) = ends.Peek();
                if ((tabs ? line.TrimStart('\t') : line).TrimEnd('\r') == word)
                {
                    ends.Dequeue();
                }
                else if (runs)
                {
                    kept.Add(line);
                }

                continue;
            }

            kept.Add(line);
            foreach (var start in HereDocumentStarts(line, ref quote))
            {
                ends.Enqueue(start);
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>The here-documents a line starts, outside quotes (which can span lines) and arithmetic.</summary>
    private static List<(string Word, bool Tabs, bool Runs)> HereDocumentStarts(string line, ref char? quote)
    {
        var starts = new List<(string, bool, bool)>();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is { } open)
            {
                if (open == '"' && c == '\\')
                {
                    i++;
                }
                else if (c == open)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '$' && string.CompareOrdinal(line, i, "$((", 0, 3) == 0)
            {
                var end = line.IndexOf("))", i + 3, StringComparison.Ordinal);
                i = end < 0 ? line.Length : end + 1;
            }
            else if (c == '<' && (i == 0 || line[i - 1] != '<') && HereDocument.Match(line, i) is { Success: true } start)
            {
                starts.Add((start.Groups["word"].Value, start.Groups["dash"].Success, FeedsAShell(line[..i])));
                i += start.Length - 1;
            }
        }

        return starts;
    }

    /// <summary>The command a here-document is fed to is a shell, here or on another machine: "bash", "sudo bash", "ssh host".</summary>
    private static bool FeedsAShell(string before)
    {
        var command = before[(before.LastIndexOfAny([';', '|', '&', '(']) + 1)..];
        return command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(w => PosixShells.Contains(Name(w)) || Name(w).Equals("ssh", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The PowerShell option <paramref name="name"/> given as <paramref name="arg"/>: one of its aliases, or a start of its
    /// name at least three characters long (PowerShell takes any start that is not ambiguous).
    /// </summary>
    private static bool IsPowerShellOption(string arg, string name, params string[] aliases) =>
        aliases.Contains(arg, StringComparer.OrdinalIgnoreCase)
        || (arg.Length >= 3 && arg[0] == '-' && name.StartsWith(arg, StringComparison.OrdinalIgnoreCase));

    /// <summary>The paths a writer writes: its -Path, -FilePath or -LiteralPath, or its first word that is no option (every one for tee).</summary>
    private static IEnumerable<string> Paths(List<string> args, bool all)
    {
        if ((Value(args, "-filepath") ?? Value(args, "-path") ?? Value(args, "-literalpath")) is { } named)
        {
            return [named];
        }

        var plain = args.Where(a => !a.StartsWith('-'));
        return all ? plain : plain.Take(1);
    }

    /// <summary>The word after the option <paramref name="name"/> (or its -Name:value), matched as PowerShell does, by its start.</summary>
    private static string? Value(List<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var colon = arg.IndexOf(':');
            var flag = colon > 1 ? arg[..colon] : arg;
            if (flag.Length > 2 && flag.StartsWith('-') && name.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
            {
                return colon > 1 ? arg[(colon + 1)..] : i + 1 < args.Count ? args[i + 1] : null;
            }
        }

        return null;
    }

    /// <summary>The command's name as it is run: "rm" of "/usr/bin/rm" or "git.exe".</summary>
    private static string Name(string word)
    {
        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        return equals > 0 && !word.StartsWith('-') && word[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private enum TokenKind
    {
        Word,
        Separator,
        Redirect,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>
    /// The command cut as its shell would, roughly: words (quotes taken off, a quoted part kept whole), the separators
    /// between commands, and redirects that write (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>2&gt;</c>; not <c>2&gt;&amp;1</c>); and
    /// what <c>$( )</c> or backticks inside double quotes run, to be read as commands of their own. A quote that is never
    /// closed is read as a character, so it hides nothing after it.
    /// </summary>
    private static (List<Token> Tokens, List<string> Inner) Tokens(string command, ShellDialect dialect)
    {
        var tokens = new List<Token>();
        var inner = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var escape = dialect switch
        {
            ShellDialect.PowerShell => '`',
            ShellDialect.Cmd => '^',
            _ => '\\',
        };

        void End()
        {
            if (inWord)
            {
                tokens.Add(new Token(TokenKind.Word, word.ToString()));
            }

            word.Clear();
            inWord = false;
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            // PowerShell's here-strings: @' … '@ and @" … "@, each mark on a line of its own.
            if (dialect == ShellDialect.PowerShell && c == '@' && i + 2 < command.Length && command[i + 1] is '\'' or '"'
                && command[i + 2] is '\n' or '\r')
            {
                var mark = command[i + 1];
                var close = command.IndexOf("\n" + mark + "@", i + 2, StringComparison.Ordinal);
                if (close >= 0)
                {
                    var body = command[(i + 2)..close];
                    word.Append(body);
                    if (mark == '"')
                    {
                        inner.AddRange(Substitutions(body, dialect));
                    }

                    inWord = true;
                    i = close + 2;
                    continue;
                }
            }

            if (c is '\'' or '"')
            {
                var close = Closing(command, i, dialect);
                if (close < 0)
                {
                    word.Append(c); // never closed: a character, not a quote
                    inWord = true;
                    continue;
                }

                var body = command[(i + 1)..close];
                if (c == '"')
                {
                    inner.AddRange(Substitutions(body, dialect));
                    body = dialect switch
                    {
                        ShellDialect.Posix => body.Replace("\\\"", "\"").Replace("\\\\", "\\"),
                        ShellDialect.PowerShell => body.Replace("`\"", "\""),
                        _ => body,
                    };
                }

                word.Append(body);
                inWord = true;
                i = close;
                continue;
            }

            // An escaped character is a plain one: "\;" is no separator. In Bash only what the shell would read otherwise is
            // escaped, so a Windows path keeps its backslashes; elsewhere an escaped line end goes on to the next line.
            if (c == escape && i + 1 < command.Length)
            {
                var next = command[i + 1];
                if (next is '\n' or '\r')
                {
                    i++;
                    End();
                    continue;
                }

                if (dialect != ShellDialect.Posix || "\"'; &|<>()$`\\".Contains(next))
                {
                    word.Append(next);
                    inWord = true;
                    i++;
                    continue;
                }
            }

            // ${HOME}, ${env:TEMP}: a variable, not a block.
            if (c == '$' && i + 1 < command.Length && command[i + 1] == '{' && command.IndexOf('}', i + 2) is var brace and > 0)
            {
                word.Append(command, i, brace - i + 1);
                inWord = true;
                i = brace;
                continue;
            }

            if (c == '>' && !(inWord && word.Length > 0 && word[^1] is '=' or '-') && !(i + 1 < command.Length && command[i + 1] == '='))
            {
                if (inWord && word.ToString() is var fd && fd.All(ch => char.IsAsciiDigit(ch) || ch == '*'))
                {
                    word.Clear();
                    inWord = false; // 2>, *>: the stream, not a word
                }

                End();
                if (i + 1 < command.Length && command[i + 1] is '>' or '|')
                {
                    i++;
                }

                if (i + 1 < command.Length && command[i + 1] == '&')
                {
                    i++; // >&1, >&2: into another stream
                    while (i + 1 < command.Length && char.IsAsciiDigit(command[i + 1]))
                    {
                        i++;
                    }

                    continue;
                }

                tokens.Add(new Token(TokenKind.Redirect, ">"));
                continue;
            }

            if (c is '\n' or '\r' or ';' or '|' or '&' or '(' or ')' or '{' or '}' || (c == '`' && dialect == ShellDialect.Posix))
            {
                End();
                tokens.Add(new Token(TokenKind.Separator, c.ToString()));
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                End();
                continue;
            }

            word.Append(c);
            inWord = true;
        }

        End();
        return (tokens, inner);
    }

    /// <summary>Where the quote that opens at <paramref name="open"/> closes, past the dialect's escapes; -1 when it never does.</summary>
    public static int Closing(string command, int open, ShellDialect dialect)
    {
        var quote = command[open];
        for (var i = open + 1; i < command.Length; i++)
        {
            if (quote == '"' && ((dialect == ShellDialect.Posix && command[i] == '\\') || (dialect == ShellDialect.PowerShell && command[i] == '`'))
                && i + 1 < command.Length)
            {
                i++; // an escaped character, a quote too
                continue;
            }

            if (command[i] == quote)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>What <c>$( … )</c> (and in Bash, backticks) inside a double-quoted string run.</summary>
    private static IEnumerable<string> Substitutions(string quoted, ShellDialect dialect)
    {
        for (var i = 0; i < quoted.Length; i++)
        {
            if (quoted[i] == '$' && i + 1 < quoted.Length && quoted[i + 1] == '(')
            {
                var depth = 0;
                var start = i + 2;
                var j = i + 1;
                for (; j < quoted.Length; j++)
                {
                    if (quoted[j] == '(')
                    {
                        depth++;
                    }
                    else if (quoted[j] == ')' && --depth == 0)
                    {
                        break;
                    }
                }

                yield return quoted[start..Math.Min(j, quoted.Length)];
                i = j;
            }
            else if (quoted[i] == '`' && dialect == ShellDialect.Posix && quoted.IndexOf('`', i + 1) is var end and > 0)
            {
                yield return quoted[(i + 1)..end];
                i = end;
            }
        }
    }
}
