using Microsoft.Win32;

namespace CodeSwitchX.UI.Settings;

/// <summary>Asks the user for a sound file to add (#307); the tests answer it themselves.</summary>
public interface ISoundFilePicker
{
    /// <summary>The WAV or MP3 picked, or null when the user cancelled. UI thread.</summary>
    string? Pick();
}

/// <summary>The Windows file dialog, showing WAV and MP3 files only.</summary>
public sealed class SoundFilePicker : ISoundFilePicker
{
    public string? Pick()
    {
        var dialog = new OpenFileDialog { Filter = "Sounds (WAV, MP3)|*.wav;*.mp3", Title = "Add a sound" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
