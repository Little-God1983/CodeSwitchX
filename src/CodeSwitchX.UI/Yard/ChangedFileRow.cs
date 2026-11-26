using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.UI.Yard;

/// <summary>One row of the panel's Changes tab (#444): a file behind the line's "N changed" pill.</summary>
public sealed record ChangedFileRow(ChangedFile File)
{
    /// <summary>What happened to it, in a word.</summary>
    public string StateText => File.Kind switch
    {
        ChangeKind.New => "new",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Conflict => "conflict",
        _ => "edited",
    };

    /// <summary>The path from the checkout's top; a rename shows where it came from.</summary>
    public string DisplayPath => File.OldPath is { } old ? $"{File.Path} ← {old}" : File.Path;

    public string FullPath => File.FullPath;

    /// <summary>The folder the file is in, full path; a new folder git lists whole is its own.</summary>
    public string FolderPath => File.IsFolder ? File.FullPath : System.IO.Path.GetDirectoryName(File.FullPath) ?? File.FullPath;

    /// <summary>The open-folder button, for a screen reader.</summary>
    public string OpenFolderName => $"Open the folder of {File.Path}";

    /// <summary>The copy-folder-path button, for a screen reader.</summary>
    public string CopyFolderName => $"Copy the folder path of {File.Path}";
}
