namespace CodeSwitchX.Voice.Audio;

public enum MicrophoneChoiceOutcome
{
    Stored,
    Relocated,
    FellBackToDefault,
    NoDevices,
}

public sealed record MicrophoneChoiceResult(MicrophoneDevice? Device, MicrophoneChoiceOutcome Outcome);

public static class MicrophoneChoice
{
    /// <summary>Picks the microphone to record from. A null <paramref name="stored"/> means "no choice yet": the default device, reported as Stored.</summary>
    public static MicrophoneChoiceResult Resolve(IReadOnlyList<MicrophoneDevice> devices, MicrophoneDevice? stored, MicrophoneDevice? systemDefault)
    {
        var (device, outcome) = Resolve<MicrophoneDevice>(devices, stored, systemDefault);
        return new MicrophoneChoiceResult(device, outcome);
    }

    /// <summary>Picks the device of any kind (a microphone, a speaker): the stored one by id, then by a name only it has, else the default.</summary>
    public static (T? Device, MicrophoneChoiceOutcome Outcome) Resolve<T>(IReadOnlyList<T> devices, T? stored, T? systemDefault)
        where T : class, IAudioDevice
    {
        if (devices.Count == 0)
        {
            return (null, MicrophoneChoiceOutcome.NoDevices);
        }

        var fallback = systemDefault ?? devices[0];
        if (stored is null)
        {
            return (fallback, MicrophoneChoiceOutcome.Stored);
        }

        var sameId = devices.FirstOrDefault(d => d.Id == stored.Id);
        if (sameId is not null)
        {
            return (sameId, MicrophoneChoiceOutcome.Stored);
        }

        var sameName = devices.Where(d => d.Name == stored.Name).Take(2).ToList();
        if (sameName.Count == 1)
        {
            return (sameName[0], MicrophoneChoiceOutcome.Relocated);
        }

        return (fallback, MicrophoneChoiceOutcome.FellBackToDefault);
    }
}
