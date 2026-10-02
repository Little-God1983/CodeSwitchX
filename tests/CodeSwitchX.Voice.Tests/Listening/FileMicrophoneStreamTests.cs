using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class FileMicrophoneStreamTests
{
    [Fact]
    public void A_file_is_fed_in_10_ms_blocks_then_silence_until_stopped()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x10, 16_000 * 2).ToArray()); // 1 s of a constant level
        using var stream = new FileMicrophoneStream(path, realTime: false);
        var frames = new List<CapturedFrames>();
        var enough = new ManualResetEventSlim();
        stream.FramesCaptured += (_, f) =>
        {
            lock (frames)
            {
                frames.Add(f);
                if (frames.Count == 150)
                {
                    enough.Set();
                }
            }
        };

        stream.Start("file");
        enough.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
        stream.Stop();

        lock (frames)
        {
            frames.ShouldAllBe(f => f.Samples16k.Length == 160);
            frames[0].Rms.ShouldBeGreaterThan(0f);
            frames[120].Rms.ShouldBe(0f); // after the file: silence
        }

        File.Delete(path);
    }
}
