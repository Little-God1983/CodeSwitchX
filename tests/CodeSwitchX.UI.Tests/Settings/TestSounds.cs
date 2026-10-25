using CodeSwitchX.UI.Settings;
using NAudio.Wave;

namespace CodeSwitchX.UI.Tests.Settings;

/// <summary>Sound files the user picks, for the Sounds page's tests (#307).</summary>
internal static class TestSounds
{
    /// <summary>A 440 Hz tone at half scale of <paramref name="seconds"/>, 16-bit mono 44.1 kHz.</summary>
    public static string Tone(string directory, string name, double seconds)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        for (var i = 0; i < (int)(44100 * seconds); i++)
        {
            writer.WriteSample(0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 44100));
        }

        return path;
    }
}

/// <summary>The file dialog, answered by the test: <see cref="Next"/>, or null as a cancel.</summary>
internal sealed class FakeSoundFiles : ISoundFilePicker
{
    public string? Next { get; set; }

    public string? Pick() => Next;
}
