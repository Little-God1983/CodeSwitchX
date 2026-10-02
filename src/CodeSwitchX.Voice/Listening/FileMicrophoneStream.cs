using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// A microphone that plays a file: 16 kHz mono 16-bit PCM (a .wav's 44-byte header skipped), in 10 ms blocks, then
/// silence until stopped. For the tests that feed the whole listener, and for the on-screen check of Open mic, which
/// nobody but the user can talk into. <paramref name="realTime"/> paces it as a microphone would.
/// </summary>
public sealed class FileMicrophoneStream(string pcmPath, bool realTime) : IMicrophoneStream, IDisposable
{
    private const int Block = 160;
    private CancellationTokenSource? _run;
    private Task _feeding = Task.CompletedTask;

    public event EventHandler<CapturedFrames>? FramesCaptured;

#pragma warning disable CS0067 // A file never fails; the event is the interface's.
    public event EventHandler<MicrophoneException>? Failed;
#pragma warning restore CS0067

    public void Start(string deviceId)
    {
        var bytes = File.ReadAllBytes(pcmPath);
        var offset = pcmPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? 44 : 0;
        var samples = new float[(bytes.Length - offset) / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, offset + (i * 2)) / 32768f;
        }

        var run = _run = new CancellationTokenSource();
        _feeding = Task.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (long n = 0; !run.IsCancellationRequested; n++)
            {
                var block = new float[Block];
                var at = n * Block;
                if (at < samples.Length)
                {
                    samples.AsSpan((int)at, (int)Math.Min(Block, samples.Length - at)).CopyTo(block);
                }

                FramesCaptured?.Invoke(this, new CapturedFrames(block, AudioMath.Rms(block)));
                if (realTime)
                {
                    var due = TimeSpan.FromMilliseconds((n + 1) * 10) - clock.Elapsed;
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due).ConfigureAwait(false);
                    }
                }
                else if (n % 100 == 0)
                {
                    await Task.Yield();
                }
            }
        });
    }

    public void Stop()
    {
        _run?.Cancel();
        _feeding.Wait(TimeSpan.FromSeconds(2));
        _run = null;
    }

    public void Dispose() => Stop();
}
