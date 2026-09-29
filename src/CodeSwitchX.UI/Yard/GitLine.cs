namespace CodeSwitchX.UI.Yard;

/// <summary>
/// One repository's line on a tile: its branch and the git state pill. A workspace file that lists folders in several
/// repositories gets a line per repository, each named after its folder; a tile with one repository leaves the name out.
/// </summary>
/// <param name="Folder">The folder's name; null on a tile with a single line.</param>
/// <param name="DirtyCount">Files with uncommitted changes; null when git could not tell.</param>
public sealed record GitLine(string? Folder, string? Branch, int? DirtyCount)
{
    /// <summary>Before the first git round: "no git" and no pill.</summary>
    public static readonly GitLine Unknown = new(null, null, null);

    public string Text => Folder is null ? Branch ?? "no git" : $"{Folder} · {Branch ?? "no git"}";

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
