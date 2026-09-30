using System.Diagnostics;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace CodeSwitchX.Voice.Dictation;

/// <summary>One shared WhisperFactory (it holds the loaded model; the library documents it as
/// reusable across processors) and one processor per clip. Transcriptions are serialised: the
/// user dictates one clip at a time and two at once would only fight for the GPU.</summary>
public sealed class WhisperDictationService(
    IWhisperModelStore store,
    IOptions<DictationOptions> options,
    ILogger<WhisperDictationService> logger) : IDictationService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public async Task<DictationResult> TranscribeAsync(ReadOnlyMemory<float> samples,
        DictationVocabulary vocabulary, bool live, CancellationToken ct)
    {
        var length = TimeSpan.FromSeconds((double)samples.Length / AudioMath.TargetRate);
        if (length < options.Value.MinimumClip)
        {
            return new DictationResult("", length);
        }

        if (!store.IsPresent)
        {
            throw new DictationModelMissingException(store.ModelPath);
        }

        await _gate.WaitAsync(ct);
        var clock = Stopwatch.StartNew();
        var loadedTheModel = _factory is null;
        try
        {
            // FromPath and CreateBuilder are both in here on purpose: Whisper.net loads the model
            // lazily, so FromPath happily returns for a file it will later refuse and the real
            // failure surfaces in CreateBuilder, with _factory already cached, which would make
            // every later clip throw the same way until the app restarted. Dropping the factory
            // on failure lets a re-downloaded model be picked up without one.
            WhisperProcessorBuilder builder;
            try
            {
                _factory ??= WhisperFactory.FromPath(store.ModelPath);
                builder = _factory.CreateBuilder().WithLanguage(options.Value.Language);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // What Whisper.net throws here is about magic numbers and native allocations and
                // means nothing to the user. Name the model, the backend it failed on, and the
                // two things actually worth trying.
                logger.LogError(ex, "Whisper could not load {Path} on {Runtime}",
                    store.ModelPath, store.LoadedRuntime ?? "(no backend chosen)");
                _factory?.Dispose();
                _factory = null;
                throw new DictationModelLoadException(store.ModelPath, store.LoadedRuntime, ex);
            }

            var prompt = VocabularyPrompt.Build(vocabulary.Words);
            if (prompt.Length > 0)
            {
                builder = builder.WithPrompt(prompt);
            }

            var parts = new List<string>();
            await using (var processor = builder.Build())
            {
                await foreach (var segment in processor.ProcessAsync(samples, ct))
                {
                    var text = segment.Text.Trim();
                    if (text.Length > 0)
                    {
                        parts.Add(text);
                    }
                }
            }

            var joined = string.Join(' ', parts);
            var corrected = TranscriptCorrector.Apply(joined, vocabulary.Corrections);
            // The elapsed time and whether the model had to be loaded are both in here because a
            // slow clip is nearly always the one that loaded the model, and without the two side
            // by side that is indistinguishable from slow recognition.
            // Two templates rather than one with a " (live)" string interpolated into it: that
            // string would arrive as a structured property and make the two kinds of line
            // impossible to filter apart.
            if (live)
            {
                logger.LogDebug(
                    "Dictation (live): {Seconds:F1}s of audio -> {Chars} chars in {Ms} ms on {Runtime}",
                    length.TotalSeconds, corrected.Length, clock.ElapsedMilliseconds,
                    store.LoadedRuntime ?? "(unknown)");
            }
            else
            {
                logger.LogInformation(
                    "Dictation: {Seconds:F1}s of audio -> {Chars} chars in {Ms} ms on {Runtime}{Load}",
                    length.TotalSeconds, corrected.Length, clock.ElapsedMilliseconds,
                    store.LoadedRuntime ?? "(unknown)",
                    loadedTheModel ? " (includes loading the model)" : "");
            }

            return new DictationResult(corrected, length);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            if (!store.IsPresent)
            {
                return;
            }

            // One second of silence, only to force the work the first real clip would otherwise
            // do: the model onto the GPU, and the backend's shaders compiled and cached. Long
            // enough to clear MinimumClip, whose default is half that; a caller that raised it past
            // a second just gets a warm-up that returns early, which costs nothing but the load.
            var sw = Stopwatch.StartNew();
            await TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, live: false, ct);
            logger.LogInformation("Dictation warm-up: {Model} ready on {Runtime} in {Ms} ms",
                store.Model, store.LoadedRuntime ?? "(unknown)", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, including cancellation. See IDictationService.WarmUpAsync:
            // nothing awaits the warm-up, and a model that will not load must not fail the caller.
            logger.LogWarning(ex, "Dictation warm-up failed; the first clip will pay the load instead");
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
