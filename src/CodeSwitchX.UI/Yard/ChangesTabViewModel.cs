using System.Globalization;
using CodeSwitchX.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The panel's third tab (#444): the files behind the line's "N changed" pill, as the last git round read them. Not a GitHub list: it
/// reads nothing itself, the Yard hands it each round's files (<see cref="Follow"/>).
/// </summary>
public sealed partial class ChangesTabViewModel : ObservableObject
{
    private ChangedFileList? _files;

    /// <summary>The files, one row each, sorted by path.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(TabAutomationName), nameof(EmptyText))]
    private IReadOnlyList<ChangedFileRow> _rows = [];

    /// <summary>The tab is in front.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabAutomationName))]
    private bool _isShown;

    /// <summary>Git gave no answer: no rows, and no made-up "No changed files." either.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(TabAutomationName), nameof(EmptyText))]
    private bool _isUnknown;

    public ChangesTabViewModel(ChangedFileList? files) => Follow(files);

    public string TabName => "Changes";

    /// <summary>The tab's count; nothing where git could not tell.</summary>
    public string CountText => IsUnknown ? string.Empty : Rows.Count.ToString(CultureInfo.InvariantCulture);

    public string TabAutomationName => string.Create(CultureInfo.InvariantCulture,
        $"{TabName}, {(IsUnknown ? "not known" : $"{Rows.Count} files")}, {(IsShown ? "shown" : "not shown")}");

    /// <summary>The tab's line when it has no rows; null when it has some.</summary>
    public string? EmptyText => IsUnknown ? "Git could not tell what changed." : Rows.Count == 0 ? "No changed files." : null;

    /// <summary>
    /// The files as git read them now; null when git could not tell. The same files again keep the rows as they are, so the list stays
    /// where it was scrolled through a round every 30 s.
    /// </summary>
    public void Follow(ChangedFileList? files)
    {
        IsUnknown = files is null;
        if (files == _files)
        {
            return;
        }

        _files = files;
        Rows = files is null ? [] : [.. files.Files.Select(f => new ChangedFileRow(f))];
    }
}
