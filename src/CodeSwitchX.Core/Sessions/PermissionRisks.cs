using System.Text;

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

    /// <summary><c>git reset --hard</c>, <c>git checkout --force</c>, <c>git restore</c>.</summary>
    DiscardsChanges,

    /// <summary>An edit, a write, a copy or a shell redirect to a path that is not under the chat's folder.</summary>
    WritesOutsideItsFolder,
}

/// <summary>
/// Finds what is risky in a command or a write, by rules rather than by a model, so what Raven says of a permission prompt
/// cannot leave it out. The rules read the command as a shell would, roughly: the command at the start of each part
/// (after <c>;</c>, <c>&amp;&amp;</c>, <c>|</c>, a bracket or a newline), what <c>bash -c</c> or <c>pwsh -Command</c> runs,
/// and where <c>&gt;</c> writes. They err on the side of naming: a word that only looks like a command can be named too.
/// </summary>
public static class PermissionRisks
{
    private static readonly HashSet<string> Deleters = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "rd", "del", "erase", "unlink", "shred", "remove-item", "ri",
    };

    /// <summary>Commands that run the rest of their words as a command.</summary>
    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "doas", "nohup", "time", "env", "command", "builtin", "exec", "xargs", "nice",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "zsh", "dash", "pwsh", "powershell", "cmd",
    };

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

    /// <summary>Where a redirect writes nothing.</summary>
    private static readonly HashSet<string> Nowhere = new(StringComparer.OrdinalIgnoreCase) { "/dev/null", "nul", "$null" };

    /// <summary>What is risky in a shell command (Bash or PowerShell) run in <paramref name="folder"/>, in the enum's order.</summary>
    /// <param name="folder">The chat's folder; null when it is not known, and then nothing is named as outside it.</param>
    public static IReadOnlyList<PermissionRisk> OfCommand(string command, string? folder)
    {
        var risks = new HashSet<PermissionRisk>();
        Scan(command, folder, risks, depth: 0);
        return Ordered(risks);
    }

    /// <summary>What is risky in an edit or a write of <paramref name="path"/>.</summary>
    /// <param name="emptiesIt">The write puts nothing over a file that is there.</param>
    public static IReadOnlyList<PermissionRisk> OfWrite(string path, string? folder, bool emptiesIt = false)
    {
        var risks = new HashSet<PermissionRisk>();
        if (emptiesIt)
        {
            risks.Add(PermissionRisk.EmptiesAFile);
        }

        WritesTo(path, folder, risks);
        return Ordered(risks);
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
    /// is not (a variable that is not set, a path no file system takes). A relative path is under the folder it runs in;
    /// <c>~</c>, <c>$HOME</c>, <c>$env:NAME</c>, <c>%NAME%</c> and Git Bash's <c>/c/…</c> are read as the machine has them.
    /// </summary>
    public static bool? IsInside(string path, string folder)
    {
        if (Expanded(path.Trim()) is not { Length: > 0 } expanded)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(folder, expanded));
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

    /// <summary>The path with its home and variables put in; null where one of them is not set.</summary>
    private static string? Expanded(string path)
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

        // Git Bash: /c/Users/… is C:\Users\…
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == '/')
        {
            return $"{char.ToUpperInvariant(path[1])}:\\{path[3..]}";
        }

        return path;
    }

    private static void WritesTo(string path, string? folder, HashSet<PermissionRisk> risks)
    {
        if (folder is { Length: > 0 } && !Nowhere.Contains(path) && IsInside(path, folder) == false)
        {
            risks.Add(PermissionRisk.WritesOutsideItsFolder);
        }
    }

    private static void Scan(string command, string? folder, HashSet<PermissionRisk> risks, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        var tokens = Tokens(command);
        var part = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.Redirect)
            {
                if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Word)
                {
                    WritesTo(tokens[++i].Text, folder, risks);
                }

                continue;
            }

            if (token.Kind == TokenKind.Separator)
            {
                Command(part, folder, risks, depth);
                part.Clear();
                continue;
            }

            part.Add(token.Text);
        }

        Command(part, folder, risks, depth);
    }

    /// <summary>One simple command: its name and its words.</summary>
    private static void Command(List<string> words, string? folder, HashSet<PermissionRisk> risks, int depth)
    {
        var at = 0;
        while (at < words.Count && (IsAssignment(words[at]) || Runners.Contains(Name(words[at]))))
        {
            at++;
            while (at < words.Count && (words[at].StartsWith('-') || IsAssignment(words[at])))
            {
                at++; // the runner's own options, env's variables
            }
        }

        if (at >= words.Count)
        {
            return;
        }

        var name = Name(words[at]);
        var args = words.Skip(at + 1).ToList();
        if (Deleters.Contains(name))
        {
            risks.Add(PermissionRisk.DeletesFiles);
        }
        else if (Shells.Contains(name))
        {
            // bash -c "…", pwsh -Command "…", cmd /c …: what it runs is a command of its own.
            var run = args.FindIndex(a => a.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Equals("/k", StringComparison.OrdinalIgnoreCase)
                || (a.StartsWith("-c", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("-config", StringComparison.OrdinalIgnoreCase)));
            if (run >= 0 && run + 1 < args.Count)
            {
                Scan(string.Join(' ', args.Skip(run + 1)), folder, risks, depth + 1);
            }
        }
        else if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            Git(args, risks);
        }
        else if (name.Equals("find", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Contains("-delete"))
            {
                risks.Add(PermissionRisk.DeletesFiles);
            }

            var exec = args.FindIndex(a => a is "-exec" or "-execdir" or "-ok" or "-okdir");
            if (exec >= 0)
            {
                Command(args.Skip(exec + 1).ToList(), folder, risks, depth + 1);
            }
        }
        else if (Writers.Contains(name))
        {
            foreach (var path in Paths(args, all: name.StartsWith("tee", StringComparison.OrdinalIgnoreCase)))
            {
                WritesTo(path, folder, risks);
            }
        }
        else if (Copiers.Contains(name))
        {
            var target = Value(args, "-destination") ?? args.LastOrDefault(a => !a.StartsWith('-'));
            if (target is not null && args.Count(a => !a.StartsWith('-')) >= 2)
            {
                WritesTo(target, folder, risks);
            }
        }
    }

    private static void Git(List<string> args, HashSet<PermissionRisk> risks)
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
                risks.Add(PermissionRisk.Pushes);
                if (Has("--force", "--force-with-lease", "--force-if-includes", "--mirror") || HasShort('f') || rest.Any(a => a.StartsWith('+')))
                {
                    risks.Add(PermissionRisk.RewritesHistory);
                }

                break;
            case "reset" when Has("--hard"):
                risks.Add(PermissionRisk.DiscardsChanges);
                break;
            case "checkout" or "switch" when Has("--force", "--discard-changes") || HasShort('f'):
            case "restore" when !Has("--staged") || Has("--worktree"):
                risks.Add(PermissionRisk.DiscardsChanges);
                break;
            case "rebase" or "filter-branch" or "filter-repo":
            case "commit" when Has("--amend"):
                risks.Add(PermissionRisk.RewritesHistory);
                break;
            case "clean":
            case "rm" when !Has("--cached"):
                risks.Add(PermissionRisk.DeletesFiles);
                break;
        }
    }

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
    /// The command cut as a shell would, roughly: words (quotes taken off, a quoted part kept whole), the separators between
    /// commands, and redirects that write (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>2&gt;</c>; not <c>2&gt;&amp;1</c>).
    /// </summary>
    private static List<Token> Tokens(string command)
    {
        var tokens = new List<Token>();
        var word = new StringBuilder();
        var inWord = false;

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
            if (c is '\'' or '"')
            {
                var close = i + 1;
                for (; close < command.Length && command[close] != c; close++)
                {
                    if (c == '"' && command[close] == '\\' && close + 1 < command.Length && command[close + 1] == '"')
                    {
                        word.Append('"');
                        close++;
                        continue;
                    }

                    word.Append(command[close]);
                }

                inWord = true;
                i = close;
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
                if (i + 1 < command.Length && command[i + 1] == '>')
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

            if (c is '\n' or '\r' or ';' or '|' or '&' or '(' or ')' or '{' or '}' or '`')
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
}
