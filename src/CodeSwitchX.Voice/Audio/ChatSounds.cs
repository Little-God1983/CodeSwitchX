using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>Plays the chats' sounds (#306); the tests use a fake.</summary>
public interface IChatSounds
{
    /// <summary>
    /// Plays the sound on the output chosen, at the sounds' level; done once it has played out, or at once when cancelled,
    /// which stops it. Never throws: a device that fails is logged, and the play is done. Any thread.
    /// </summary>
    Task PlayAsync(string soundId, CancellationToken ct);

    /// <summary>How long the sound plays; zero for one not shipped.</summary>
    TimeSpan LengthOf(string soundId);
}

/// <summary>
/// The sounds of the pool (<see cref="SoundLibrary"/>), each play opening the output chosen, or the default device of the moment,
/// off the caller's thread (opening a waking device can take a while) and closing it when done.
/// </summary>
public sealed class ChatSounds(IAudioOutput output, AudioLevels levels, TimeProvider time, ILogger<ChatSounds> logger, SoundLibrary? library = null)
    : IChatSounds
{
    private readonly SoundLibrary _library = library ?? new SoundLibrary(null);

    /// <summary>
    /// How much longer than the sound a play may take, for a waking device to open: past it the play is stopped, so a device
    /// that never says it stopped does not hold the floor, and Raven's speech after it, for good.
    /// </summary>
    public static readonly TimeSpan PlayMargin = TimeSpan.FromSeconds(5);

    public TimeSpan LengthOf(string soundId) => _library.LengthOf(soundId);

    public async Task PlayAsync(string soundId, CancellationToken ct)
    {
        using var capped = new CancellationTokenSource(LengthOf(soundId) + PlayMargin, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, capped.Token);
        await PlayUntilAsync(soundId, stop.Token).ConfigureAwait(false);
    }

    private async Task PlayUntilAsync(string soundId, CancellationToken ct)
    {
        if (!_library.Has(soundId))
        {
            logger.LogWarning("No sound {Sound} to play", soundId);
            return;
        }

        if (levels.Sounds <= 0f)
        {
            // Nothing would be heard: no device is woken for it, but the sound takes its time, as if it played.
            try
            {
                await Task.Delay(LengthOf(soundId), time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped before its end.
            }

            return;
        }

        if (ct.IsCancellationRequested)
        {
            return; // stopped before it began: no device is woken for it
        }

        IWavePlayer? player = null;
        try
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opening = Task.Run(() =>
            {
                var (pcm, format) = _library.Load(soundId);
                return output.Open(new RawSourceWaveStream(pcm, 0, pcm.Length, format), () => levels.Sounds, 120);
            }, CancellationToken.None);
            try
            {
                // A headset waking can hold the open for seconds: a stop does not wait for it, and the device is let go once open.
                player = await opening.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _ = opening.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        AudioOutput.DisposeQuietly(t.Result);
                    }
                    else
                    {
                        logger.LogInformation(t.Exception?.GetBaseException(), "A sound stopped while its device opened; the open failed");
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return; // stopped just as the device opened: closed unheard
            }

            player.PlaybackStopped += (_, _) => ended.TrySetResult();
            player.Play();
            await using (ct.Register(() => ended.TrySetResult()))
            {
                await ended.Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Playing the sound {Sound} failed", soundId);
        }
        finally
        {
            AudioOutput.DisposeQuietly(player); // stops it too, off the caller's thread
        }
    }
}

/// <summary>
/// Which sound each slot plays (#306), as Settings picked it: an id of the pool (<see cref="SoundLibrary"/>), or null for
/// none. Starts on the slots' defaults; a sound of the user's whose file went missing plays the slot's default (#307). Any thread.
/// </summary>
public sealed class SoundPicks(SoundLibrary? library = null)
{
    private readonly SoundLibrary _library = library ?? new SoundLibrary(null);

    private readonly ConcurrentDictionary<SoundSlot, string?> _picks =
        new(Enum.GetValues<SoundSlot>().Select(s => KeyValuePair.Create(s, (string?)ShippedSounds.DefaultFor(s))));

    public string? For(SoundSlot slot) =>
        _picks.GetValueOrDefault(slot) is { } id && !_library.Has(id) ? ShippedSounds.DefaultFor(slot) : _picks.GetValueOrDefault(slot);

    public void Set(SoundSlot slot, string? id) => _picks[slot] = id;
}
