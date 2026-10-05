namespace CodeSwitchX.Core.Yard;

/// <summary>A name the user says for a model, and the full id it stands for.</summary>
public sealed record ModelAlias(string Name, string Id);

/// <summary>
/// The models and effort levels a chat started by voice can run with. The user says a short name ("Fable", "Opus"); the
/// alias table, which Settings lets them edit, turns it into a full model id. Full ids are passed to Claude Code, never
/// its own aliases: those move to newer models without notice, and an older CLI maps them to older ones.
/// </summary>
public static class ChatModels
{
    public static readonly IReadOnlyList<ModelAlias> DefaultAliases =
    [
        new("Fable", "claude-fable-5-1"),
        new("Opus", "claude-opus-5-5"),
        new("Sonnet", "claude-sonnet-5-5"),
        new("Haiku", "claude-haiku-4-5-20251001"),
    ];

    /// <summary>What <c>claude --effort</c> takes, lowest first.</summary>
    public static readonly IReadOnlyList<string> EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>One alias a line: <c>Fable = claude-fable-5-1</c>.</summary>
    public static string FormatAliases(IEnumerable<ModelAlias> aliases) =>
        string.Join(Environment.NewLine, aliases.Select(a => $"{a.Name} = {a.Id}"));

    /// <summary>
    /// The aliases of the table as typed in Settings: one a line, name and id split by <c>=</c> or <c>:</c>. Lines without
    /// both are left out; of two lines with the same name, the first counts. A table with none left is the default one.
    /// </summary>
    public static IReadOnlyList<ModelAlias> ParseAliases(string? text)
    {
        var aliases = new List<ModelAlias>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var split = line.IndexOfAny(['=', ':']);
            if (split <= 0)
            {
                continue;
            }

            var name = line[..split].Trim();
            var id = line[(split + 1)..].Trim();
            if (name.Length > 0 && id.Length > 0 && !id.Any(char.IsWhiteSpace) && !aliases.Any(a => Same(a.Name, name)))
            {
                aliases.Add(new ModelAlias(name, id));
            }
        }

        return aliases.Count > 0 ? aliases : DefaultAliases;
    }

    /// <summary>
    /// The model id a spoken or typed name means: an alias ("fable", "Opus"), an alias with its version said after it
    /// ("Opus 5.5"), or a full id ("claude-opus-5-5"). Null when it is none of these.
    /// </summary>
    public static string? ResolveModel(string? said, IReadOnlyList<ModelAlias> aliases)
    {
        var key = WorkspaceMatcher.Squash(said ?? "");
        if (key.Length == 0)
        {
            return null;
        }

        if ((AliasNamed(said, aliases) ?? aliases.FirstOrDefault(a => WorkspaceMatcher.Squash(a.Id) == key)) is { } exact)
        {
            return exact.Id;
        }

        // "opus55", "fable 5.1": the alias, then its own version. Another version ("Opus 4.1") is another model, which
        // the alias does not stand for.
        if (aliases.FirstOrDefault(a => WorkspaceMatcher.Squash(a.Name) is { Length: > 0 } name && key.StartsWith(name, StringComparison.Ordinal)
                && key[name.Length..] is { Length: > 0 } version && version == string.Concat(VersionOf(a.Id).Version)) is { } versioned)
        {
            return versioned.Id;
        }

        var id = said!.Trim().ToLowerInvariant();
        return id.StartsWith("claude-", StringComparison.Ordinal) && !id.Any(char.IsWhiteSpace) ? id : null;
    }

    /// <summary>The alias whose name it is, said by itself ("opus", "Opus."); null for a version or an id said.</summary>
    public static ModelAlias? AliasNamed(string? said, IReadOnlyList<ModelAlias> aliases)
    {
        var key = WorkspaceMatcher.Squash(said ?? "");
        return key.Length == 0 ? null : aliases.FirstOrDefault(a => WorkspaceMatcher.Squash(a.Name) == key);
    }

    /// <summary>
    /// How a model id reads on screen: <c>claude-fable-5-1</c> as "Fable 5.1", <c>claude-haiku-4-5-20251001</c> as "Haiku
    /// 4.5" (the date left out). An id of another shape is shown as it is.
    /// </summary>
    public static string DisplayName(string modelId)
    {
        var parts = modelId.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase) || !parts[1].All(char.IsLetter))
        {
            return modelId;
        }

        var (version, after) = VersionOf(modelId);
        var rest = after.Select(p => p.Length >= 8 && p[..8].All(char.IsAsciiDigit) ? p[8..] : p).ToList(); // the date left out
        var name = char.ToUpperInvariant(parts[1][0]) + parts[1][1..].ToLowerInvariant();
        return string.Join(" ", new[] { name, string.Join(".", version) }.Concat(rest).Where(p => p.Length > 0));
    }

    /// <summary>
    /// The parts of an id's version, and what comes after them: <c>claude-haiku-4-5-20251001</c> has 4 and 5, then the
    /// date, which is no part of it; <c>claude-opus-5-5[1m]</c> has 5 and 5, then "[1m]".
    /// </summary>
    private static (List<string> Version, List<string> After) VersionOf(string modelId)
    {
        var parts = modelId.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries).Skip(2).ToList();
        List<string> version = [];
        for (var i = 0; i < parts.Count; i++)
        {
            var digits = parts[i].TakeWhile(char.IsAsciiDigit).Count();
            if (digits is 0 or >= 8)
            {
                return (version, parts[i..]);
            }

            version.Add(parts[i][..digits]);
            if (digits < parts[i].Length)
            {
                return (version, [parts[i][digits..], .. parts[(i + 1)..]]);
            }
        }

        return (version, []);
    }

    /// <summary>Whether the user means Claude Code's own default, for a model or an effort: "default", "Claude Code's default".</summary>
    public static bool IsDefault(string? said) =>
        WorkspaceMatcher.Squash(said ?? "") is "default" or "claudecodesdefault" or "claudecodedefault" or "claudedefault";

    /// <summary>
    /// The effort level a spoken or typed word means: the level itself, or how it is said out loud ("extra high",
    /// "maximum", "mid"). Null when it is none.
    /// </summary>
    public static string? ResolveEffort(string? said) => WorkspaceMatcher.Squash(said ?? "") switch
    {
        "low" or "lowest" or "minimal" or "minimum" => "low",
        "medium" or "mid" or "normal" => "medium",
        "high" => "high",
        "xhigh" or "extrahigh" or "veryhigh" or "higher" => "xhigh",
        "max" or "maximum" or "highest" => "max",
        _ => null,
    };

    /// <summary>
    /// Why each row of the table, as Settings shows it, is left out by <see cref="ParseAliases"/>, or null for a row that
    /// counts and for a row not filled in at all (one just added). The rules are the parser's: name and id both given,
    /// no <c>=</c> or <c>:</c> in the name, no space in the id, and of two rows with the same name the first counts.
    /// </summary>
    public static IReadOnlyList<string?> RowProblems(IReadOnlyList<ModelAlias> rows)
    {
        var problems = new List<string?>();
        var counted = new List<string>();
        foreach (var row in rows)
        {
            var name = row.Name.Trim();
            var id = row.Id.Trim();
            string? problem = null;
            if (name.Length == 0 && id.Length == 0)
            {
            }
            else if (name.Length == 0 || id.Length == 0)
            {
                problem = "Not used: fill in both the name and the model id.";
            }
            else if (name.IndexOfAny(['=', ':']) >= 0)
            {
                problem = "Not used: a name can't contain = or :.";
            }
            else if (id.Any(char.IsWhiteSpace))
            {
                problem = "Not used: a model id has no spaces.";
            }
            else if (counted.Any(c => Same(c, name)))
            {
                problem = $"Not used: {name} is in the table already, and the first one counts.";
            }
            else
            {
                counted.Add(name);
            }

            problems.Add(problem);
        }

        return problems;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || string.Equals(WorkspaceMatcher.Squash(a), WorkspaceMatcher.Squash(b), StringComparison.Ordinal);
}
