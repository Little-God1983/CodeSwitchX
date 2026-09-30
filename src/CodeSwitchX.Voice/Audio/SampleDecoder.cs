using NAudio.Dmo;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

public static class SampleDecoder
{
    /// <summary>Decodes a WASAPI buffer (IEEE float 32, or PCM 16/24/32) to mono floats.</summary>
    public static float[] ToMonoFloats(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var count = Math.Min(bytesRecorded, buffer.Length) / bytesPerSample;
        var samples = new float[count];
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

        for (var i = 0; i < count; i++)
        {
            var offset = i * bytesPerSample;
            samples[i] = (isFloat, format.BitsPerSample) switch
            {
                (true, 32) => BitConverter.ToSingle(buffer, offset),
                (false, 16) => BitConverter.ToInt16(buffer, offset) / 32768f,
                (false, 24) => (((buffer[offset + 2] << 24) | (buffer[offset + 1] << 16) | (buffer[offset] << 8)) >> 8) / 8388608f,
                (false, 32) => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => throw new NotSupportedException($"Unsupported sample format: {format.Encoding}, {format.BitsPerSample} bit."),
            };
        }

        return AudioMath.ToMono(samples, format.Channels);
    }
}
