using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice.Dictation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeSwitchX.UI.Settings;

/// <summary>A speech-to-text model on the Listening page: its size, where it stands, and its own Download.</summary>
public sealed partial class SpeechToTextRow : ObservableObject
{
    public SpeechToTextRow(WhisperModel model, string description, string size, Func<WhisperModel, Task> download)
    {
        Model = model;
        Description = description;
        Size = size;
        DownloadCommand = new AsyncRelayCommand(() => download(Model));
    }

    [ObservableProperty] private ModelLamp _lamp = new(ModelDot.Grey, "…", "");

    /// <summary>Not on this PC and not downloading: the row offers its Download.</summary>
    [ObservableProperty] private bool _canDownload;

    /// <summary>0..1 while it downloads, else null.</summary>
    [ObservableProperty] private double? _progress;

    public WhisperModel Model { get; }

    /// <summary>"Large v3 Turbo": the row's title.</summary>
    public string Name => ModelLamp.RowNameOf(Model);

    public string Description { get; }

    public string Size { get; }

    public IAsyncRelayCommand DownloadCommand { get; }

    /// <summary>
    /// Shows where the model stands: downloading, not on this PC, or on it; the model in use shows the dictation's own
    /// state (<paramref name="inUse"/>), asleep, loading, ready or failed.
    /// </summary>
    public void Show(ModelDownload? downloading, bool present, ModelLamp? inUse)
    {
        Progress = downloading?.Bytes.Fraction;
        CanDownload = downloading is null && !present;
        Lamp = downloading is { } d
            ? new ModelLamp(ModelDot.Yellow, $"downloading {d.Bytes.Fraction:P0}", $"Downloading {ModelLamp.NameOf(Model)}: {d.Bytes}", d.Bytes.Fraction)
            : !present
                ? new ModelLamp(ModelDot.Red, "not downloaded", $"{ModelLamp.NameOf(Model)} is not on this PC yet.")
                : inUse ?? new ModelLamp(ModelDot.Grey, "on this PC", $"{ModelLamp.NameOf(Model)} is on this PC. Pick it to use it.");
    }
}

/// <summary>A row of the model names table: the name said, and the model id it starts.</summary>
public sealed partial class AliasRow : ObservableObject
{
    public AliasRow(string name, string id, Action<AliasRow> changed, Action<AliasRow> remove)
    {
        _name = name;
        _id = id;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Name) or nameof(Id))
            {
                changed(this);
            }
        };
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _id;

    /// <summary>Why the row is not used (a name twice, a space in the id), or null when it counts.</summary>
    [ObservableProperty] private string? _problem;

    public IRelayCommand RemoveCommand { get; }
}
