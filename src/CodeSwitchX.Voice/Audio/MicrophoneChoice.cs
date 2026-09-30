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
        if (devices.Count == 0)
        {
            return new MicrophoneChoiceResult(null, MicrophoneChoiceOutcome.NoDevices);
        }

        var fallback = systemDefault ?? devices[0];
        if (stored is null)
        {
            return new MicrophoneChoiceResult(fallback, MicrophoneChoiceOutcome.Stored);
        }

        var sameId = devices.FirstOrDefault(d => d.Id == stored.Id);
        if (sameId is not null)
        {
            return new MicrophoneChoiceResult(sameId, MicrophoneChoiceOutcome.Stored);
        }

        var sameName = devices.Where(d => d.Name == stored.Name).Take(2).ToList();
        if (sameName.Count == 1)
        {
            return new MicrophoneChoiceResult(sameName[0], MicrophoneChoiceOutcome.Relocated);
        }

        return new MicrophoneChoiceResult(fallback, MicrophoneChoiceOutcome.FellBackToDefault);
    }
}
