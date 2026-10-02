using System.Diagnostics;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace CodeSwitchX.Voice.Dictation;

/// <summary>One shared WhisperFactory (it holds the loaded model; the library documents it as
/// reusable across processors) and one processor per clip. Transcriptions are serialised: the
/// user dictates one clip at a time and two at once would only fight for the GPU.
///
/// <para>Everything Whisper does runs on the thread pool, never on the caller's thread: loading
/// the model takes seconds, and the caller is usually the UI thread.</para></summary>
public sealed class WhisperDictationService : IDictationService, IDisposable
{
    private readonly IWhisperModelStore store;
    private readonly IOptions<DictationOptions> options;
    private readonly ILogger<WhisperDictationService> logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _warmUpLock = new();

    /// <summary>Taken while the status is worked out and told, so listeners hear the changes in their order.</summary>
    private readonly Lock _telling = new();
    private volatile WhisperFactory? _factory;
    private Task<bool>? _warmUp;
    private ModelFileKey? _failedLoad;
    private string? _failedReason;
    private volatile bool _loading;
    private DictationStatus? _told;
    private bool _disposed;

    public WhisperDictationService(IWhisperModelStore store, IOptions<DictationOptions> options, ILogger<WhisperDictationService> logger)
    {
        this.store = store;
        this.options = options;
        this.logger = logger;
        store.DownloadChanged += (_, _) => Tell();
        store.ModelChanged += (_, _) => _ = Task.Run(SwitchModelAsync);
    }

    public event EventHandler<DictationStatus>? StatusChanged;

    public DictationStatus Status
    {
        get
        {
            var model = store.Model;
            if (store.Download is { } download && download.Model == model)
            {
                return new DictationStatus(DictationState.Downloading, model, Bytes: download.Bytes);
            }

            if (!store.IsPresent)
            {
                return new DictationStatus(DictationState.NotDownloaded, model);
            }

            if (_loading)
            {
                return new DictationStatus(DictationState.Loading, model);
            }

            if (_factory is not null)
            {
                return new DictationStatus(DictationState.Ready, model);
            }

            lock (_warmUpLock)
            {
                if (_failedLoad is not null && _failedLoad == ModelFileKey.Of(store.ModelPath))
                {
                    return new DictationStatus(DictationState.Failed, model, _failedReason);
                }
            }

            return new DictationStatus(DictationState.Asleep, model);
        }
    }

    /// <summary>Works the status out again, and tells it if it changed. Never throws.</summary>
    private void Tell()
    {
        lock (_telling)
        {
            DictationStatus status;
            try
            {
                status = Status;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not tell where the speech model stands");
                return;
            }

            if (status == _told)
            {
                return;
            }

            _told = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    /// <summary>
    /// Another model was picked: the one loaded is freed once no clip uses it, and the new one is warmed up if it is on
    /// disk and one was loaded or warming up before. The model read from the settings at the start is not: the startup
    /// warm-up loads it after its delay. On the thread pool: Settings picks it on the UI thread, and freeing waits for a
    /// running transcription.
    /// </summary>
    private async Task SwitchModelAsync()
    {
        try
        {
            bool warm;
            lock (_warmUpLock)
            {
                warm = _factory is not null || _warmUp is not null;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _factory?.Dispose();
                _factory = null;
            }
            finally
            {
                _gate.Release();
            }

            lock (_warmUpLock)
            {
                _warmUp = null;
            }

            Tell();
            if (warm && store.IsPresent)
            {
                await WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Switching the speech model failed");
        }
    }

    /// <summary>How long <see cref="Dispose"/> waits for a running transcription before it gives up
    /// on freeing the model. Only tests shorten it.</summary>
    internal TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<DictationResult> TranscribeAsync(ReadOnlyMemory<float> samples,
        DictationVocabulary vocabulary, CancellationToken ct)
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

        // Task.Run rather than ConfigureAwait alone: a free gate completes WaitAsync synchronously,
        // and everything after it, the model load included, would then run on the caller's thread.
        return await Task.Run(() => TranscribeOnPoolAsync(samples, vocabulary, length, ct), ct)
            .ConfigureAwait(false);
    }

    private async Task<DictationResult> TranscribeOnPoolAsync(ReadOnlyMemory<float> samples,
        DictationVocabulary vocabulary, TimeSpan length, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var loadedTheModel = _factory is null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // FromPath and CreateBuilder are both in here on purpose: Whisper.net loads the model
            // lazily, so FromPath happily returns for a file it will later refuse and the real
            // failure surfaces in CreateBuilder, with _factory already cached, which would make
            // every later clip throw the same way until the app restarted. Dropping the factory
            // on failure lets a re-downloaded model be picked up without one.
            WhisperProcessorBuilder builder;
            if (loadedTheModel)
            {
                _loading = true;
                Tell();
            }

            try
            {
                // Ready is told only once the clip below has gone through: until then it is loading.
                _factory ??= WhisperFactory.FromPath(store.ModelPath);
                builder = _factory.CreateBuilder().WithLanguage(options.Value.Language);
                RememberFailedLoad(null, null);
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
                var failure = new DictationModelLoadException(store.ModelPath, store.LoadedRuntime, ex);
                RememberFailedLoad(ModelFileKey.Of(store.ModelPath), failure.Message);
                _loading = false;
                Tell();
                throw failure;
            }

            var prompt = VocabularyPrompt.Build(vocabulary.Words);
            if (prompt.Length > 0)
            {
                builder = builder.WithPrompt(prompt);
            }

            var parts = new List<string>();
            var processor = builder.Build();
            await using (processor.ConfigureAwait(false))
            {
                await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
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
            logger.LogInformation(
                "Dictation: {Seconds:F1}s of audio -> {Chars} chars in {Ms} ms on {Runtime}{Load}",
                length.TotalSeconds, corrected.Length, clock.ElapsedMilliseconds,
                store.LoadedRuntime ?? "(unknown)",
                loadedTheModel ? " (includes loading the model)" : "");

            return new DictationResult(corrected, length);
        }
        finally
        {
            var loaded = loadedTheModel && _loading;
            _loading = false;
            _gate.Release();
            if (loaded)
            {
                Tell(); // ready, or asleep again after a clip that failed past the load
            }
        }
    }

    /// <summary>Warms up once per process. A caller arriving while the warm-up runs waits for that
    /// same run rather than starting a second one. A warm-up that found no model, or failed, is
    /// tried again by the next caller: the model may have been downloaded since. A model file that
    /// already failed to load, by a warm-up or a transcription, is not loaded again while its length
    /// and last-write time are unchanged: the warm-up returns false at once, and only a
    /// transcription, which the user waits on and must see fail, tries it again.</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        Task<bool> warmUp;
        lock (_warmUpLock)
        {
            if (_warmUp is null or { IsCompletedSuccessfully: true, Result: false })
            {
                _warmUp = Task.Run(() => WarmUpOnPoolAsync(ct), CancellationToken.None);
            }

            warmUp = _warmUp;
        }

        await warmUp.ConfigureAwait(false);
    }

    /// <summary>True when the model is loaded and has decoded speech once.</summary>
    private async Task<bool> WarmUpOnPoolAsync(CancellationToken ct)
    {
        try
        {
            if (!store.IsPresent)
            {
                return false;
            }

            if (_factory is not null)
            {
                return true; // a real clip got here first and paid for the load
            }

            if (FailedLoadOf(ModelFileKey.Of(store.ModelPath)))
            {
                logger.LogDebug("Dictation warm-up skipped: {Path} failed to load and has not changed since", store.ModelPath);
                return false;
            }

            // Real speech, not silence: on a clip with nobody talking Whisper's decoding keeps
            // retrying and took anywhere from 6 to 23 s (2026-09-30, RTX on Vulkan), where this
            // one-second sample takes 3 s and leaves the next real clip at about 0.2 s.
            var sw = Stopwatch.StartNew();
            await TranscribeAsync(WarmUpSpeech.Load(), DictationVocabulary.Empty, ct)
                .ConfigureAwait(false);
            logger.LogInformation("Dictation warm-up: {Model} ready on {Runtime} in {Ms} ms",
                store.Model, store.LoadedRuntime ?? "(unknown)", sw.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, including cancellation. See IDictationService.WarmUpAsync:
            // nothing awaits the warm-up, and a model that will not load must not fail the caller.
            logger.LogWarning(ex, "Dictation warm-up failed; the first clip will pay the load instead");
            return false;
        }
    }

    private void RememberFailedLoad(ModelFileKey? key, string? reason)
    {
        lock (_warmUpLock)
        {
            _failedLoad = key;
            _failedReason = reason;
        }
    }

    private bool FailedLoadOf(ModelFileKey? key)
    {
        lock (_warmUpLock)
        {
            return key is not null && key == _failedLoad;
        }
    }

    /// <summary>Tells one model file from its replacement: a new download or copy changes the length or the write time.</summary>
    private sealed record ModelFileKey(long Length, DateTime LastWriteUtc)
    {
        /// <summary>Null when the file cannot be read, which never matches a remembered failure.</summary>
        public static ModelFileKey? Of(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? new ModelFileKey(file.Length, file.LastWriteTimeUtc) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>Frees the model, but never under a running inference: that is a native
    /// use-after-free, and the app exits right after a release often enough to hit it. A
    /// transcription still running after <see cref="DisposeTimeout"/> keeps the model, and the
    /// process exit takes it instead: a stuck GPU must not hang the exit.</summary>
    public void Dispose()
    {
        if (!_gate.Wait(DisposeTimeout))
        {
            logger.LogWarning("A transcription was still running after {Seconds} s; the speech model is left to the process exit",
                DisposeTimeout.TotalSeconds);
            return;
        }

        try
        {
            if (!_disposed)
            {
                _disposed = true;
                _factory?.Dispose();
                _factory = null;
            }
        }
        finally
        {
            // The gate itself stays: a transcription queued behind this one takes it next and
            // fails with ObjectDisposedException rather than waiting forever on a disposed gate.
            _gate.Release();
        }
    }
}
