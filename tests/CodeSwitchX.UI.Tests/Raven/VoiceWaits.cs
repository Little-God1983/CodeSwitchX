using LittleGod.Speech.Speaking;

namespace CodeSwitchX.UI.Tests.Raven;

internal static class VoiceWaits
{
    /// <summary>
    /// Once the voice has nothing left to say or to make: its replies played out or dropped. The package keeps its own wait
    /// internal (#328); the public <see cref="IReplyVoice.IsBusy"/> is all it waited on.
    /// </summary>
    public static async Task WhenQuietAsync(this IReplyVoice voice)
    {
        while (voice.IsBusy)
        {
            await Task.Delay(10);
        }
    }
}
