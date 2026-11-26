namespace CodeSwitchX.Core.Workspaces;

/// <summary>What happened to a changed file, in a word the Changes tab shows (#444).</summary>
public enum ChangeKind
{
    Edited,
    New,
    Deleted,
    Renamed,
    Conflict,
}

/// <summary>
/// One file behind a tile's "N changed" pill (#444), as <c>git status --porcelain=v2</c> lists it.
/// </summary>
/// <param name="Path">As git gives it: relative to the checkout's top, <c>/</c>-separated, unquoted. A new folder git lists as one entry keeps its trailing <c>/</c>.</param>
/// <param name="FullPath">The file or folder on disk, full path, without a trailing separator.</param>
/// <param name="OldPath">Where a renamed or copied file came from, as <paramref name="Path"/> is given; null for any other change.</param>
public sealed record ChangedFile(string Path, string FullPath, ChangeKind Kind, string? OldPath = null)
{
    /// <summary>A new folder git lists whole, not file by file (<c>--untracked-files=normal</c>).</summary>
    public bool IsFolder => Path.EndsWith('/');
}

/// <summary>
/// The changed files of a checkout (#444), sorted by path. Equal by its files, not by reference: a git round that reads the same
/// files again leaves a tile's lines as they are (<c>WorkspaceTileViewModel</c> compares them) and the tab's rows where they were.
/// </summary>
public sealed class ChangedFileList : IEquatable<ChangedFileList>
{
    public static readonly ChangedFileList Empty = new([]);

    public ChangedFileList(IEnumerable<ChangedFile> files)
    {
        Files = [.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Path, StringComparer.Ordinal)];
    }

    public IReadOnlyList<ChangedFile> Files { get; }

    public bool Equals(ChangedFileList? other) => other is not null && (ReferenceEquals(this, other) || Files.SequenceEqual(other.Files));

    public override bool Equals(object? obj) => Equals(obj as ChangedFileList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Files.Count);
        foreach (var file in Files)
        {
            hash.Add(file);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(ChangedFileList? left, ChangedFileList? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ChangedFileList? left, ChangedFileList? right) => !(left == right);
}
