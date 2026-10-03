using System.Runtime.CompilerServices;

namespace CodeSwitchX.Voice.Speech;

/// <summary>One engine's voice, as <see cref="SpeechEngines"/> drives it.</summary>
public interface ISpeechEngineVoice : ITextToSpeech
{
    SpeechEngine Engine { get; }

    /// <summary>Installs it if it is not, then starts it, even soon after a failure: the user asked. Returns at once and never throws.</summary>
    void Install();

    /// <summary>Stops it, and gives up an install or load going on. Returns at once and never throws.</summary>
    void Stop();

    /// <summary>Whether it is installed with the model picked, so that starting it downloads nothing. Reads the disk.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Says whether it is on disk (<see cref="TextToSpeechState.Off"/>) or not (<see cref="TextToSpeechState.NotInstalled"/>)
    /// while it is neither running nor getting ready. Returns at once and never throws.
    /// </summary>
    void CheckInstall();
}

/// <summary>Where one engine stands.</summary>
public sealed record EngineStatus(SpeechEngine Engine, TextToSpeechStatus Status);

/// <summary>
/// Speaks with the engine <see cref="SpeechSettings.Engine"/> picks, and with none while none is: then Raven answers in
/// text only. Picking another engine stops the one before, so it frees its memory; the new one starts when Raven next
/// needs it. Settings → Voice sees every engine through it, picked or not.
/// </summary>
public sealed class SpeechEngines : ITextToSpeech, IDisposable
{
    public static readonly TextToSpeechStatus NoEngine = new(TextToSpeechState.NoEngine);

    private readonly SpeechSettings _settings;
    private readonly IReadOnlyDictionary<SpeechEngine, ISpeechEngineVoice> _engines;

    public SpeechEngines(SpeechSettings settings, IEnumerable<ISpeechEngineVoice> engines)
    {
        _settings = settings;
        _engines = engines.ToDictionary(e => e.Engine);
        foreach (var engine in _engines.Values)
        {
            engine.StatusChanged += (_, status) => OnEngineStatus(engine.Engine, status);
        }

        _settings.EngineChanged += (_, _) => OnEngineChanged();
    }

    private ISpeechEngineVoice? Current => _settings.Engine is { } engine && _engines.TryGetValue(engine, out var voice) ? voice : null;

    public TextToSpeechStatus Status => Current?.Status ?? NoEngine;

    /// <summary>Changes of the picked engine's status, and the new one's when another is picked.</summary>
    public event EventHandler<TextToSpeechStatus>? StatusChanged;

    /// <summary>Changes of every engine's status, picked or not; on any thread.</summary>
    public event EventHandler<EngineStatus>? EngineStatusChanged;

    public TextToSpeechStatus StatusOf(SpeechEngine engine) => _engines[engine].Status;

    /// <summary>Whether <paramref name="engine"/> is installed with its model. Reads the disk: not on the UI thread.</summary>
    public bool IsInstalled(SpeechEngine engine) => _engines[engine].IsInstalled;

    public void Prepare(bool install) => Current?.Prepare(install);

    /// <summary>The Voice page's install: <paramref name="engine"/> is installed if needed and started.</summary>
    public void Install(SpeechEngine engine) => _engines[engine].Install();

    /// <summary>The Voice page's cancel: an install or load of <paramref name="engine"/> is given up.</summary>
    public void Cancel(SpeechEngine engine) => _engines[engine].Stop();

    /// <summary>Has every engine say whether it is on disk; once at the start, before anything else asks them.</summary>
    public void CheckInstalls()
    {
        foreach (var engine in _engines.Values)
        {
            engine.CheckInstall();
        }
    }

    public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        var current = Current ?? throw new TextToSpeechNotReadyException(NoEngine);
        await foreach (var chunk in current.SpeakAsync(text, ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public void Recover() => Current?.Recover();

    private void OnEngineStatus(SpeechEngine engine, TextToSpeechStatus status)
    {
        EngineStatusChanged?.Invoke(this, new EngineStatus(engine, status));
        if (_settings.Engine == engine)
        {
            StatusChanged?.Invoke(this, status);
        }
    }

    private void OnEngineChanged()
    {
        var picked = _settings.Engine;
        foreach (var engine in _engines.Values.Where(e => e.Engine != picked))
        {
            engine.Stop();
        }

        StatusChanged?.Invoke(this, Status);
    }

    /// <summary>Stops every engine's sidecar.</summary>
    public void Dispose()
    {
        foreach (var engine in _engines.Values.OfType<IDisposable>())
        {
            engine.Dispose();
        }
    }
}
