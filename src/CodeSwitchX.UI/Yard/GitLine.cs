namespace CodeSwitchX.UI.Yard;

/// <summary>
/// One repository's line on a tile: its branch and the git state pill. A workspace file that lists folders in several
/// repositories gets a line per repository, each named after its folder; a tile with one repository leaves the name out.
/// </summary>
/// <param name="Folder">The folder's name, as the workspace file gives it or its own; null on a tile with a single line.</param>
/// <param name="DirtyCount">Files with uncommitted changes; null when git could not tell.</param>
/// <param name="Path">The folder, full path: the tooltip shows it, so two folders with the same name tell apart. Null before the first git round.</param>
public sealed record GitLine(string? Folder, string? Branch, int? DirtyCount, string? Path = null)
{
    /// <summary>Before the first git round: "no git" and no pill.</summary>
    public static readonly GitLine Unknown = new(null, null, null);

    public string BranchText => Branch ?? "no git";

    /// <summary>Between the folder's name and the branch; empty on a tile with a single line.</summary>
    public string Separator => Folder is null ? string.Empty : " · ";

    /// <summary>The whole line, untrimmed; what the tests read.</summary>
    public string Text => Folder + Separator + BranchText;

    /// <summary>The whole line, and the folder's path under it.</summary>
    public string ToolTipText => Path is null ? Text : $"{Text}\n{Path}";

    public string? GitStateLabel => Label(DirtyCount);

    /// <summary>
    /// "clean", or how many files have uncommitted changes (edited, staged or new). Null when git could not tell, so no
    /// pill: a made-up "clean" would hide changes.
    /// </summary>
    public static string? Label(int? dirtyCount) => dirtyCount switch
    {
        null => null,
        0 => "clean",
        var n => $"{n} changed",
    };
}
