using System.Globalization;

namespace CodeSwitchX.Core.Yard;

/// <param name="MatchedName">The name that matched: the workspace's own, or the name of one of its folders.</param>
/// <param name="Score">1 for the same name, down to <see cref="WorkspaceMatcher.Threshold"/>.</param>
public sealed record WorkspaceMatch(YardWorkspace Workspace, string MatchedName, double Score);

/// <summary>
/// Finds the workspaces a spoken or typed name means. The name arrives through speech recognition, so it is compared
/// without case, spaces or punctuation ("Diffusion Nexus", "diffusion-nexus" and "DiffusionNexus" are one name), with a few
/// letters' slack for a misheard one, and against the names of a workspace's folders as well as its own: "Diffusion Nexus"
/// finds Diffusion-Full through its DiffusionNexus folder.
/// </summary>
public static class WorkspaceMatcher
{
    /// <summary>The lowest score that counts as a match.</summary>
    public const double Threshold = 0.7;

    /// <summary>How much of what was said a name must make up to match by being part of it.</summary>
    private const double MostOfTheQuery = 0.6;

    /// <summary>
    /// The best of the matches: those with the top score, or within a hair of it. What a question about one workspace is
    /// about; the weaker matches are other workspaces that only look a little alike.
    /// </summary>
    public static IReadOnlyList<WorkspaceMatch> Best(IReadOnlyList<WorkspaceMatch> matches) =>
        matches.Count == 0 ? [] : matches.Where(m => m.Score >= matches[0].Score - 0.01).ToList();

    /// <summary>The workspaces that match, best first; a workspace appears once, under its best-matching name.</summary>
    public static IReadOnlyList<WorkspaceMatch> Find(string query, IEnumerable<YardWorkspace> workspaces)
    {
        // A number ("3", "number three", "Chat drei") means the workspace that has it, by its own name; when none has it,
        // the words are matched as a name like any other.
        if (SpokenNumber.TryRead(query, out var number) && number > 0
            && workspaces.FirstOrDefault(w => w.Number == number) is { } numbered)
        {
            return [new WorkspaceMatch(numbered, numbered.Name, 1)];
        }

        var key = Squash(query);
        if (key.Length == 0)
        {
            return [];
        }

        var words = Words(query);
        var matches = new List<WorkspaceMatch>();
        foreach (var workspace in workspaces)
        {
            WorkspaceMatch? best = null;
            foreach (var name in NamesOf(workspace))
            {
                var score = Score(key, words, Squash(name));
                if (score >= Threshold && (best is null || score > best.Score))
                {
                    best = new WorkspaceMatch(workspace, name, score);
                }
            }

            if (best is not null)
            {
                matches.Add(best);
            }
        }

        return matches.OrderByDescending(m => m.Score).ThenBy(m => m.Workspace.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The folder a match was made through: the one whose name matched, or the workspace's first folder (its root) when
    /// its own name did. Where a chat for the name runs: "Diffusion Nexus" in Diffusion-Full's DiffusionNexus folder.
    /// </summary>
    public static YardFolder FolderOf(WorkspaceMatch match)
    {
        var workspace = match.Workspace;
        var root = workspace.Folders.Count > 0 ? workspace.Folders[0] : new YardFolder(workspace.Name, workspace.RootPath);
        if (match.MatchedName == workspace.Name)
        {
            return root;
        }

        return workspace.Folders.FirstOrDefault(f => f.Name == match.MatchedName || OwnName(f) == match.MatchedName) ?? root;
    }

    /// <summary>The workspace's folder a name means best, matched like a workspace name; null when none matches.</summary>
    public static YardFolder? FindFolder(string query, YardWorkspace workspace)
    {
        var key = Squash(query);
        if (key.Length == 0)
        {
            return null;
        }

        var words = Words(query);
        return workspace.Folders
            .Select(f => (Folder: f, Score: Math.Max(Score(key, words, Squash(f.Name)), Score(key, words, Squash(OwnName(f))))))
            .Where(f => f.Score >= Threshold)
            .OrderByDescending(f => f.Score)
            .Select(f => f.Folder)
            .FirstOrDefault();
    }

    private static string OwnName(YardFolder folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder.Path));

    /// <summary>The workspace's name, then each folder's name and, where the workspace file names it differently, its own.</summary>
    private static IEnumerable<string> NamesOf(YardWorkspace workspace)
    {
        yield return workspace.Name;
        foreach (var folder in workspace.Folders)
        {
            yield return folder.Name;
            var own = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder.Path));
            if (own.Length > 0 && !own.Equals(folder.Name, StringComparison.OrdinalIgnoreCase))
            {
                yield return own;
            }
        }
    }

    private static double Score(string key, IReadOnlyList<string> words, string name)
    {
        if (name.Length == 0)
        {
            return 0;
        }

        if (key == name)
        {
            return 1;
        }

        // What was said is part of the name: "diffusion" in "diffusionfull". The closer the lengths, the better the match.
        if (key.Length >= 3 && key.Length < name.Length && name.Contains(key, StringComparison.Ordinal))
        {
            return 0.8 + (0.1 * key.Length / name.Length);
        }

        // The name is part of what was said, and most of it: "codeswitchx app" for CodeSwitchX. A short name that is only
        // a piece of it ("code" or "switch" in "codeswitchx", "api" in "backend api") is no match.
        if (name.Length >= MostOfTheQuery * key.Length && key.Contains(name, StringComparison.Ordinal))
        {
            return 0.8 + (0.1 * name.Length / key.Length);
        }

        // A misheard name: "code switch ex" for CodeSwitchX.
        var similarity = 1 - ((double)Distance(key, name) / Math.Max(key.Length, name.Length));
        if (similarity >= Threshold)
        {
            return 0.7 + (0.2 * (similarity - Threshold) / (1 - Threshold));
        }

        // Every word said is in the name, in any order: "installer nexus" for DiffusionNexus.Installer.SDK. Words under three
        // letters ("of", "ui") are left out rather than let fail the rule: they are in almost any name, or in none.
        var kept = words.Where(w => w.Length >= 3).ToList();
        return kept.Count > 1 && kept.All(w => name.Contains(w, StringComparison.Ordinal)) ? Threshold : 0;
    }

    /// <summary>Lower case, letters and digits only.</summary>
    internal static string Squash(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit).Select(c => char.ToLower(c, CultureInfo.InvariantCulture)));

    /// <summary>The words of the text, each squashed.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || !char.IsLetterOrDigit(text[i]))
            {
                if (i > start)
                {
                    words.Add(Squash(text[start..i]));
                }

                start = i + 1;
            }
        }

        return words;
    }

    /// <summary>Levenshtein distance: the fewest letters to add, drop or change to turn one into the other.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var change = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + change);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
