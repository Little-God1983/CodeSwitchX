namespace CodeSwitchX.Voice.Dictation;

/// <summary>
/// "Hello Raven.", about a second of it, for the warm-up to decode. Rendered once with Windows' built-in voice
/// (System.Speech) and stored in WarmUpSpeech.pcm as 16 kHz mono 16-bit little-endian PCM, trimmed to the speech plus
/// 100 ms either side. An embedded file rather than rendering it at run time: System.Speech is a test-only package here.
/// </summary>
internal static class WarmUpSpeech
{
    internal const string ResourceName = "CodeSwitchX.Voice.WarmUpSpeech.pcm";

    /// <summary>The sample as floats in -1..1, ready for Whisper.</summary>
    public static float[] Load()
    {
        using var stream = typeof(WarmUpSpeech).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes.Slice(i * 2, 2)) / 32768f;
        }

        return samples;
    }
}
