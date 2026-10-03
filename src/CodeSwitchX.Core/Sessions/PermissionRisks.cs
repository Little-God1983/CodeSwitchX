using System.Globalization;
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

    /// <summary>Extensions of files a checkout of one word is taken to be of: "git checkout README.md".</summary>
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".xaml", ".resx", ".json", ".md", ".txt", ".js", ".mjs", ".cjs", ".ts",
        ".tsx", ".jsx", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".cc", ".yml", ".yaml", ".xml", ".html",
        ".htm", ".css", ".scss", ".less", ".ps1", ".psm1", ".sh", ".bat", ".cmd", ".toml", ".ini", ".cfg", ".config", ".lock", ".sql",
        ".vue", ".svelte", ".php", ".swift", ".gradle", ".png", ".svg", ".editorconfig", ".gitignore",
    };

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

        /// <summary>What the command wrote to each file so far, read when it then runs the file as a script.</summary>
        private readonly Dictionary<string, string> _scripts = new(StringComparer.OrdinalIgnoreCase);

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

            var (text, bodies) = dialect == ShellDialect.Posix ? WithoutHereDocuments(command) : (command, []);
            var tokens = Tokens(text, dialect);
            var part = new List<string>();
            var targets = new List<string>(); // where the part's redirects write
            string? body = null; // the part's here-document: its input
            string? piped = null; // what the part before a "|" writes: the next part reads it
            var subshells = new Stack<string?>();
            string? lastSeparator = null;
            var inBackticks = false;
            string? beforeBackticks = null;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Redirect)
                {
                    if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Word)
                    {
                        RunInner(tokens[++i], dialect, depth);
                        WritesTo(tokens[i].Text);
                        targets.Add(tokens[i].Text);
                    }

                    continue;
                }

                if (token.Kind == TokenKind.Separator)
                {
                    // "|&" pipes standard error too: the pipe goes on.
                    if (token.Text == "&" && lastSeparator == "|" && part.Count == 0)
                    {
                        lastSeparator = "|&";
                        continue;
                    }

                    var written = Run(part, dialect, depth, body ?? piped, targets);
                    piped = token.Text == "|" ? written : null;
                    part.Clear();
                    targets.Clear();
                    body = null;
                    lastSeparator = token.Text;
                    // A cd in ( … ), $( … ) or backticks is the subshell's own; in PowerShell and cmd brackets only group.
                    if (dialect == ShellDialect.Posix && token.Text == "(")
                    {
                        subshells.Push(_here);
                    }
                    else if (dialect == ShellDialect.Posix && token.Text == ")" && subshells.Count > 0)
                    {
                        _here = subshells.Pop();
                    }
                    else if (dialect == ShellDialect.Posix && token.Text == "`")
                    {
                        if (inBackticks)
                        {
                            _here = beforeBackticks;
                        }
                        else
                        {
                            beforeBackticks = _here;
                        }

                        inBackticks = !inBackticks;
                    }

                    continue;
                }

                if (token.Text.StartsWith(HereDocumentMark, StringComparison.Ordinal))
                {
                    body = bodies[int.Parse(token.Text.AsSpan(HereDocumentMark.Length), CultureInfo.InvariantCulture)];
                    continue;
                }

                RunInner(token, dialect, depth);
                part.Add(token.Text);
            }

            Run(part, dialect, depth, body ?? piped, targets);
        }

        /// <summary>What <c>$( )</c> or backticks inside a quoted word run: a subshell of its own, where the word is.</summary>
        private void RunInner(Token token, ShellDialect dialect, int depth)
        {
            foreach (var run in token.Inner ?? [])
            {
                Child(run, dialect, depth);
            }
        }

        /// <summary>
        /// One simple command: its name and its words, with <paramref name="fed"/> as its input (a here-document, or what
        /// the part before a <c>|</c> writes), run as commands by a shell that reads them (<c>bash &lt;&lt;EOF</c>,
        /// <c>echo "git reset --hard" | bash</c>, <c>@' … '@ | iex</c>), on the other machine when ssh or a container
        /// does. Returns what the command writes, for the next part of a pipe and for the files its redirects go to.
        /// </summary>
        private string? Run(List<string> words, ShellDialect dialect, int depth, string? fed = null, List<string>? targets = null)
        {
            if (Named(words) is not { } named)
            {
                return null;
            }

            var (name, args, chdir, word) = named;
            var here = _here;
            if (chdir is not null)
            {
                _here = Full(chdir, _here); // env -C, sudo -D: the command runs there
            }

            var runs = RunsItsInput(name, args);
            if (fed is not null && runs is { } input)
            {
                Child(fed, input, depth, elsewhere: RunsElsewhere(name, args));
            }

            Simple(name, args, word, dialect, depth);
            if (chdir is not null)
            {
                _here = here;
            }

            if (runs is not null || Written(name, args, words, fed) is not { } written)
            {
                return null;
            }

            foreach (var target in targets ?? [])
            {
                _scripts[ScriptKey(target)] = written; // cat > fix.sh <<EOF: read when "bash fix.sh" follows
            }

            return written;
        }

        /// <summary>What the command wrote to <paramref name="path"/> earlier; null when nothing.</summary>
        private string? Script(string path) => _scripts.TryGetValue(ScriptKey(path), out var body) ? body : null;

        private string ScriptKey(string path) => Full(path, _here) ?? path;

        /// <summary>
        /// The command's name and its words, past assignments, runners and their options; the folder a runner runs it
        /// in (<c>env -C</c>, <c>sudo -D</c>), if one does; and the word the name came from (<c>./fix.sh</c>). Null when
        /// there is no command.
        /// </summary>
        public static (string Name, List<string> Args, string? Chdir, string Word)? Named(List<string> words)
        {
            var at = 0;
            string? chdir = null;
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

                var runner = Name(words[at]).ToLowerInvariant();
                at++;
                while (at < words.Count && (words[at].StartsWith('-') || IsAssignment(words[at])))
                {
                    var option = words[at];
                    if ((runner == "env" && option is "-C" or "--chdir") || (runner == "sudo" && option is "-D" or "--chdir"))
                    {
                        chdir = at + 1 < words.Count ? words[at + 1] : null;
                    }
                    else if (runner is "env" or "sudo" && option.StartsWith("--chdir=", StringComparison.Ordinal))
                    {
                        chdir = option["--chdir=".Length..];
                    }

                    at += valued.Contains(option, StringComparer.Ordinal) ? 2 : 1; // an option's value is no command
                }

                if (RunnersWithAnArgument.Contains(runner))
                {
                    at++; // timeout's duration
                }
            }

            return at >= words.Count ? null : (Name(words[at]), words.Skip(at + 1).ToList(), chdir, words[at]);
        }

        /// <summary>
        /// The dialect a command reads its input in when it runs it as commands, whether piped in or a here-document;
        /// null when it does not. <c>bash</c>, <c>sh -s</c>, <c>ssh host</c>, <c>ssh host bash</c>, <c>docker exec -i app sh</c>
        /// and <c>iex</c> do; <c>bash script.sh</c>, <c>bash -c "…"</c> and <c>ssh host make</c> run something else.
        /// </summary>
        public static ShellDialect? RunsItsInput(string name, List<string> args)
        {
            if (PosixShells.Contains(name))
            {
                var runsOther = args.Any(IsRunFlag) || (ShellScript(args) is not null && !args.Contains("-s"));
                return runsOther ? null : ShellDialect.Posix;
            }

            if (name.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                // What runs there reads what comes in, in any part of its pipeline: "cd /app && bash -s".
                return SshCommand(args) is not { } remote ? ShellDialect.Posix
                    : Parts(Tokens(remote, ShellDialect.Posix)).Select(part => Named(part.Words) is { } named ? RunsItsInput(named.Name, named.Args) : null)
                        .FirstOrDefault(d => d is not null);
            }

            // docker exec -i app sh, kubectl exec -i pod -- bash: the command run in the container reads what comes in.
            if (ContainerCommand(name, args) is { } inside)
            {
                return Named(inside) is { } named ? RunsItsInput(named.Name, named.Args) : null;
            }

            return IsInvokeExpression(name) && args.Count == 0 ? ShellDialect.PowerShell : null;
        }

        /// <summary>A command that runs what it is given on another machine, or in a container: nowhere known here.</summary>
        private static bool RunsElsewhere(string name, List<string> args) =>
            name.Equals("ssh", StringComparison.OrdinalIgnoreCase) || ContainerCommand(name, args) is not null;

        /// <summary>
        /// The script a shell runs: its first word that is no option; null when there is none, so it reads its input.
        /// An option's value (<c>-o pipefail</c>, <c>-euo pipefail</c>, <c>+euo pipefail</c>, <c>--rcfile x.rc</c>) is no script.
        /// </summary>
        private static string? ShellScript(List<string> args)
        {
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg.Length > 0 && arg[0] is '-' or '+')
                {
                    i += arg is "-o" or "-O" or "+o" or "+O" or "--rcfile" or "--init-file" || (arg.Length > 2 && arg[1] != '-' && arg[^1] is 'o' or 'O') ? 1 : 0;
                    continue;
                }

                return arg;
            }

            return null;
        }

        /// <summary>Options of docker, podman, compose and kubectl, and of their <c>exec</c>, that take a value.</summary>
        private static readonly HashSet<string> ContainerValued = new(StringComparer.Ordinal)
        {
            "-H", "--host", "-c", "--context", "-l", "--log-level", "--config", "--tlscacert", "--tlscert", "--tlskey", // docker
            "-f", "--file", "-p", "--project-name", "--profile", "--env-file", "--project-directory", "--ansi", "--progress", "--parallel", // compose
            "-n", "--namespace", "--kubeconfig", "--cluster", "--user", "-s", "--server", "--token", "--as", "--as-group", "--request-timeout", // kubectl
            "--cache-dir", "--certificate-authority", "--client-certificate", "--client-key", "--tls-server-name", "-v",
            "-u", "-w", "--workdir", "-e", "--env", "--detach-keys", "--index", "--container", "--filename", "--pod-running-timeout", // exec
        };

        /// <summary>
        /// The command <c>docker exec</c>, <c>docker compose exec</c>, <c>podman exec</c> or <c>kubectl exec</c> runs in its
        /// container or pod, past their options (<c>kubectl -n prod exec -it pod -- sh</c>); null when <paramref name="name"/>
        /// is none of them, or runs nothing.
        /// </summary>
        private static List<string>? ContainerCommand(string name, List<string> args)
        {
            var tool = name.ToLowerInvariant();
            var compose = tool is "docker-compose" or "podman-compose";
            if (!compose && tool is not ("docker" or "podman" or "nerdctl" or "kubectl"))
            {
                return null;
            }

            var at = PastOptions(args, 0);
            if (!compose && at < args.Count && args[at] == "compose")
            {
                at = PastOptions(args, at + 1);
            }

            if (at >= args.Count || args[at] != "exec")
            {
                return null;
            }

            // After "--" the command is sure; otherwise the first word that is no option is the container or pod.
            var dashes = args.IndexOf("--", at + 1);
            var command = dashes >= 0 ? dashes + 1 : PastOptions(args, at + 1) + 1;
            return command < args.Count ? args.Skip(command).ToList() : null;

            static int PastOptions(List<string> args, int at)
            {
                while (at < args.Count && args[at].StartsWith('-'))
                {
                    at += ContainerValued.Contains(args[at]) ? 2 : 1;
                }

                return at;
            }
        }

        /// <summary>What ssh runs on the other machine: the words after its options and the host; null when it runs a shell there.</summary>
        public static string? SshCommand(List<string> args)
        {
            const string valued = "bcDEeFIiJLlmOopQRSWw"; // its options that take a value
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg.StartsWith('-'))
                {
                    i += arg.Length == 2 && valued.Contains(arg[1]) ? 1 : 0;
                    continue;
                }

                return i + 1 < args.Count ? string.Join(' ', args.Skip(i + 1)) : null; // arg is the host
            }

            return null;
        }

        private static bool IsInvokeExpression(string name) =>
            name.Equals("iex", StringComparison.OrdinalIgnoreCase) || name.Equals("invoke-expression", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// What a part writes out, to be read as commands or written to a file: echo's or Write-Output's words, what
        /// printf makes of its format, what a filter (<c>cat</c>, <c>grep</c>, <c>sort</c>) was <paramref name="fed"/>,
        /// near enough, or a lone string (<c>'git reset --hard' | iex</c>).
        /// </summary>
        private static string? Written(string name, List<string> args, List<string> words, string? fed)
        {
            if (name.ToLowerInvariant() is "echo" or "printf" or "write-output" or "write" or "write-host")
            {
                // Its options come first; "--force" after them is a word it writes.
                var options = args.TakeWhile(a => a.StartsWith('-')).ToList();
                var plain = args.Skip(options.Count).ToList();
                if (name.Equals("printf", StringComparison.OrdinalIgnoreCase))
                {
                    return plain.Count == 0 ? null : Printf(Unescaped(plain[0]), plain.Skip(1).ToList());
                }

                var text = string.Join(' ', plain);
                return options.Any(a => a.Length > 1 && a.Contains('e') && a[1] != '-') ? Unescaped(text) : text; // echo -e
            }

            return fed ?? (words.Count == 1 ? words[0] : null);
        }

        /// <summary>A conversion in printf's format (<c>%s</c>, <c>%-10s</c>, <c>%d</c>), or <c>%%</c>.</summary>
        private static readonly Regex Conversion = new(@"%(?:%|[-+ #0]*\d*(?:\.\d+)?[a-zA-Z])", RegexOptions.CultureInvariant);

        /// <summary>
        /// What printf writes: each conversion takes the next argument, and the format is repeated while arguments remain
        /// (<c>printf '%s\n' a b</c> writes a and b on lines of their own; <c>printf '%s %s\n' git push</c> writes one line).
        /// </summary>
        private static string Printf(string format, List<string> args)
        {
            var text = new StringBuilder();
            var next = 0;
            do
            {
                var before = next;
                text.Append(Conversion.Replace(format, m => m.Value == "%%" ? "%" : next < args.Count ? args[next++] : ""));
                if (next == before)
                {
                    break; // no conversion: the format is written once
                }
            }
            while (next < args.Count);

            return text.ToString();
        }

        /// <summary>The text with its <c>\n</c> and <c>\t</c> as a line break and a tab, as printf and echo -e write them.</summary>
        private static string Unescaped(string text) => text.Replace("\\n", "\n").Replace("\\t", "\t");

        /// <summary>bash's -c, and -lc, -ec, -xc: the next word is what it runs.</summary>
        private static bool IsRunFlag(string arg) => arg.Length > 1 && arg[0] == '-' && arg[1] != '-' && arg.Contains('c');

        /// <summary>
        /// A command another shell runs, in <paramref name="start"/> if given, or <paramref name="elsewhere"/> (on another
        /// machine, nowhere known here): a cd in it is that shell's own.
        /// </summary>
        private void Child(string command, ShellDialect dialect, int depth, string? start = null, bool elsewhere = false)
        {
            var here = _here;
            if (elsewhere)
            {
                _here = null;
            }
            else if (start is not null)
            {
                _here = Full(start, _here);
            }

            Command(command, dialect, depth + 1);
            _here = here;
        }

        private void Simple(string name, List<string> args, string word, ShellDialect dialect, int depth)
        {
            if (Script(word) is { } own)
            {
                // ./fix.sh, written by the command before: run as the script it is.
                Child(own, word.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? ShellDialect.PowerShell : ShellDialect.Posix, depth);
            }
            else if (Deleters.Contains(name))
            {
                Risks.Add(PermissionRisk.DeletesFiles);
            }
            else if (PosixShells.Contains(name))
            {
                // bash -c "…", bash -lc "…": what it runs is a command of its own; "bash fix.sh" runs what was written to it.
                var run = args.FindIndex(IsRunFlag);
                if (run >= 0 && run + 1 < args.Count)
                {
                    Child(args[run + 1], ShellDialect.Posix, depth);
                }
                else if (run < 0 && !args.Contains("-s") && ShellScript(args) is { } script && Script(script) is { } body)
                {
                    Child(body, ShellDialect.Posix, depth);
                }
            }
            else if (name is "source" or ".")
            {
                if (args.FirstOrDefault() is { } script && Script(script) is { } body)
                {
                    Command(body, ShellDialect.Posix, depth + 1); // in this shell: a cd in it stays
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
            else if (name.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                if (SshCommand(args) is { } remote)
                {
                    Child(remote, ShellDialect.Posix, depth, elsewhere: true);
                }
            }
            else if (IsInvokeExpression(name))
            {
                Command(Value(args, "-command") ?? string.Join(' ', args), ShellDialect.PowerShell, depth + 1);
            }
            else if (ContainerCommand(name, args) is { } inside)
            {
                // docker exec app rm -rf /data: the command runs in the container, nowhere known here.
                if (depth < 4)
                {
                    var here = _here;
                    _here = null;
                    Run(inside, ShellDialect.Posix, depth + 1);
                    _here = here;
                }
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
                    Run(args.Skip(exec + 1).ToList(), dialect, depth + 1);
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
                Push(args, dialect);
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
            string? start = null; // -WorkingDirectory: where what it runs starts
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (IsPowerShellOption(arg, "-command", "-c") || IsPowerShellOption(arg, "-commandwithargs", "-cwa"))
                {
                    Child(string.Join(' ', args.Skip(i + 1)), ShellDialect.PowerShell, depth, start);
                    return;
                }

                if (IsPowerShellOption(arg, "-workingdirectory", "-wd"))
                {
                    start = i + 1 < args.Count ? args[++i] : null;
                    continue;
                }

                if (IsPowerShellOption(arg, "-encodedcommand", "-e", "-ec"))
                {
                    try
                    {
                        Child(i + 1 < args.Count ? Encoding.Unicode.GetString(Convert.FromBase64String(args[i + 1])) : "", ShellDialect.PowerShell, depth, start);
                    }
                    catch (FormatException)
                    {
                        // not base64: nothing to read
                    }

                    return;
                }

                if (IsPowerShellOption(arg, "-file", "-f"))
                {
                    if (i + 1 < args.Count && Script(args[i + 1]) is { } script)
                    {
                        Child(script, ShellDialect.PowerShell, depth, start); // written by the command before
                    }

                    return;
                }

                if (PowerShellValued.Any(o => IsPowerShellOption(arg, o.Name, o.Aliases)))
                {
                    i++; // its value
                    continue;
                }

                if (!arg.StartsWith('-') && name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                {
                    Child(string.Join(' ', args.Skip(i)), ShellDialect.PowerShell, depth, start);
                    return;
                }
            }
        }

        /// <summary>
        /// A <c>pushd</c>: to a folder, which is pushed; <c>+N</c>/<c>-N</c> turns the stack; <c>-n</c> pushes without
        /// moving; bare, Bash swaps the two top folders and Push-Location pushes where it is. <c>-StackName</c> names a
        /// stack, which is kept as one.
        /// </summary>
        private void Push(List<string> args, ShellDialect dialect)
        {
            if (dialect == ShellDialect.Posix && args.FirstOrDefault(a => a.Length > 1 && a[0] is '+' or '-' && a[1..].All(char.IsAsciiDigit)) is { } turn)
            {
                var stack = new List<string?> { _here };
                stack.AddRange(_pushed);
                var by = int.Parse(turn[1..], System.Globalization.CultureInfo.InvariantCulture) % stack.Count;
                var top = turn[0] == '+' ? by : (stack.Count - 1 - by);
                stack = [.. stack.Skip(top), .. stack.Take(top)];
                _here = stack[0];
                _pushed.Clear();
                foreach (var folder in stack.Skip(1).Reverse())
                {
                    _pushed.Push(folder);
                }

                return;
            }

            var stackName = Value(args, "-stackname");
            var to = Value(args, "-path") ?? Value(args, "-literalpath")
                ?? args.FirstOrDefault(a => (!a.StartsWith('-') || a == "-") && a != stackName);
            if (to is null)
            {
                if (dialect == ShellDialect.Posix && _pushed.Count > 0)
                {
                    (_here, var below) = (_pushed.Pop(), _here);
                    _pushed.Push(below);
                }
                else if (dialect != ShellDialect.Posix)
                {
                    _pushed.Push(_here);
                }

                return;
            }

            if (args.Contains("-n"))
            {
                _pushed.Push(to == "-" ? null : Full(to, _here)); // on the stack, not moved to
                return;
            }

            _pushed.Push(_here);
            Move([to], dialect);
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
        /// One word is a path when it is a file or folder in <paramref name="repository"/>, or, with no folder in it, ends in
        /// a known file type's extension ("README.md"; not "release-2.x" or "bump/Newtonsoft.Json"). A branch is made with
        /// -b, -B or --orphan.
        /// </summary>
        private static bool ChecksOutPaths(List<string> rest, string? repository)
        {
            if (rest.Any(a => a is "-b" or "-B" or "--orphan"))
            {
                return false;
            }

            var plain = rest.Where(a => !a.StartsWith('-')).ToList();
            if (plain.Count != 1)
            {
                return plain.Count >= 2;
            }

            var word = plain[0];
            var full = Full(word, repository);
            if (full is not null && (File.Exists(full) || Directory.Exists(full)))
            {
                return true;
            }

            // Where it cannot be looked for, a word with a folder in it is a path by its extension too: "src/App.cs".
            var inAFolder = word.IndexOfAny(['/', '\\']) >= 0;
            return (!inAFolder || full is null) && FileExtensions.Contains(Path.GetExtension(word));
        }

        private void Git(List<string> args)
        {
            var at = 0;
            var repository = _here; // git -C <path> works there
            while (at < args.Count && args[at].StartsWith('-'))
            {
                if (args[at] == "-C" && at + 1 < args.Count)
                {
                    repository = Full(args[at + 1], repository);
                }

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
                case "checkout" when Has("--force", "--discard-changes", "--", ".", "--patch") || HasShort('f') || HasShort('p') || ChecksOutPaths(rest, repository):
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

    /// <summary>What stands for a here-document in the command once its body is taken out, followed by the body's number.</summary>
    private const string HereDocumentMark = "<<\u0001";

    /// <summary>
    /// The command with its here-documents' bodies taken out, each start replaced by a mark that names its body, so that
    /// the part it belongs to gets the body as its input: <c>bash &lt;&lt;EOF</c> runs it, <c>cat &gt; fix.sh &lt;&lt;EOF</c>
    /// writes it, <c>cat &lt;&lt;EOF | bash</c> pipes it on. A <c>&lt;&lt;</c> inside quotes or <c>$(( ))</c> starts none.
    /// </summary>
    private static (string Command, List<string> Bodies) WithoutHereDocuments(string command)
    {
        var bodies = new List<StringBuilder>();
        if (!command.Contains("<<", StringComparison.Ordinal))
        {
            return (command, []);
        }

        var kept = new List<string>();
        var open = new Queue<(string Word, bool Tabs, StringBuilder Body)>();
        char? quote = null;
        foreach (var line in command.Split('\n'))
        {
            if (open.Count > 0)
            {
                var (word, tabs, body) = open.Peek();
                var text = (tabs ? line.TrimStart('\t') : line).TrimEnd('\r');
                if (text == word)
                {
                    open.Dequeue();
                }
                else
                {
                    body.Append(body.Length > 0 ? "\n" : "").Append(text);
                }

                continue;
            }

            var starts = HereDocumentStarts(line, ref quote);
            var marked = line;
            for (var k = starts.Count - 1; k >= 0; k--)
            {
                marked = $"{marked[..starts[k].Index]} {HereDocumentMark}{bodies.Count + k} {marked[(starts[k].Index + starts[k].Length)..]}";
            }

            kept.Add(marked);
            foreach (var (word, tabs, _, _) in starts)
            {
                var body = new StringBuilder();
                bodies.Add(body);
                open.Enqueue((word, tabs, body));
            }
        }

        return (string.Join('\n', kept), bodies.Select(b => b.ToString()).ToList());
    }

    /// <summary>The here-documents a line starts, outside quotes (which can span lines) and arithmetic: each with where its start is.</summary>
    private static List<(string Word, bool Tabs, int Index, int Length)> HereDocumentStarts(string line, ref char? quote)
    {
        var starts = new List<(string, bool, int, int)>();
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
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]) || line[i - 1] is ';' or '|' or '&' or '('))
            {
                break; // a comment to the end of the line
            }
            else if (c == '(' && i + 1 < line.Length && line[i + 1] == '(')
            {
                // Arithmetic, (( … )) or $(( … )): "1<<BIT" is a shift.
                var end = line.IndexOf("))", i + 2, StringComparison.Ordinal);
                i = end < 0 ? line.Length : end + 1;
            }
            else if (c == '<' && (i == 0 || line[i - 1] != '<') && HereDocument.Match(line, i) is { Success: true } start)
            {
                starts.Add((start.Groups["word"].Value, start.Groups["dash"].Success, i, start.Length));
                i += start.Length - 1;
            }
        }

        return starts;
    }

    /// <summary>The words of each simple command (a redirect's target is none), with the separator before it ("|" for a pipe).</summary>
    private static List<(List<string> Words, string? After)> Parts(List<Token> tokens)
    {
        var parts = new List<(List<string>, string?)>();
        var words = new List<string>();
        string? after = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.Separator)
            {
                parts.Add((words, after));
                words = [];
                after = token.Text;
            }
            else if (token.Kind == TokenKind.Redirect)
            {
                i += i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Word ? 1 : 0;
            }
            else
            {
                words.Add(token.Text);
            }
        }

        parts.Add((words, after));
        return parts;
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

    /// <param name="Inner">What <c>$( )</c> or backticks inside the word's quotes run; null when nothing.</param>
    private readonly record struct Token(TokenKind Kind, string Text, List<string>? Inner = null);

    /// <summary>
    /// The command cut as its shell would, roughly: words (quotes taken off, a quoted part kept whole), the separators
    /// between commands, and redirects that write (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>2&gt;</c>; not <c>2&gt;&amp;1</c>); and
    /// what <c>$( )</c> or backticks inside double quotes run, to be read as commands of their own. A quote that is never
    /// closed is read as a character, so it hides nothing after it.
    /// </summary>
    private static List<Token> Tokens(string command, ShellDialect dialect)
    {
        var tokens = new List<Token>();
        List<string>? inner = null;
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
                tokens.Add(new Token(TokenKind.Word, word.ToString(), inner));
            }

            word.Clear();
            inner = null;
            inWord = false;
        }

        void Runs(string quoted)
        {
            foreach (var run in Substitutions(quoted, dialect))
            {
                (inner ??= []).Add(run);
            }
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
                        Runs(body);
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
                    Runs(body);
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

            // A comment, to the end of the line: "# Don't touch it" hides no command behind its apostrophe.
            if (c == '#' && !inWord && dialect != ShellDialect.Cmd)
            {
                var end = command.IndexOf('\n', i);
                i = (end < 0 ? command.Length : end) - 1;
                continue;
            }

            // PowerShell's block comment, <# … #>.
            if (dialect == ShellDialect.PowerShell && c == '<' && i + 1 < command.Length && command[i + 1] == '#')
            {
                var end = command.IndexOf("#>", i + 2, StringComparison.Ordinal);
                i = end < 0 ? command.Length : end + 1;
                continue;
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
        return tokens;
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
