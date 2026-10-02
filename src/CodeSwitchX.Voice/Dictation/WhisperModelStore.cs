using Microsoft.Extensions.Options;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace CodeSwitchX.Voice.Dictation;

/// <summary>Which model is used, where the model files live and how they get there. The download goes to a
/// ".partial" file and is renamed only when complete, so a half file is never mistaken for a
/// model: IsPresent looks at the final name only. "Complete" is checked, not assumed: a proxy or
/// CDN closing a length-less response early ends the stream without an error. One download per
/// model at a time, whichever model is in use: the Raven panel and Settings asking for the same one share it,
/// and a model picked again while its download still runs joins that download.</summary>
public sealed class WhisperModelStore(IOptions<DictationOptions> options) : IWhisperModelStore
{
    private readonly Lock _lock = new();
    private WhisperModel _model = options.Value.Model;

    /// <summary>The downloads running, one per model at most.</summary>
    private readonly Dictionary<WhisperModel, Downloading> _downloads = [];

    public WhisperModel Model
    {
        get
        {
            lock (_lock)
            {
                return _model;
            }
        }
        set
        {
            lock (_lock)
            {
                if (_model == value)
                {
                    return;
                }

                _model = value;
            }

            ModelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ModelChanged;

    public string ModelPath => PathOf(Model);

    public bool IsPresent => File.Exists(ModelPath);

    public ModelDownload? Download
    {
        get
        {
            lock (_lock)
            {
                return _downloads.TryGetValue(_model, out var d) ? new ModelDownload(d.Model, d.Bytes) : null;
            }
        }
    }

    public event EventHandler? DownloadChanged;

    private string PathOf(WhisperModel model) => Path.Combine(options.Value.ModelFolder, FileName(model));

    // Whisper.net picks a native backend the first time a WhisperFactory is built and remembers
    // it process-wide, so this is null until someone has dictated once.
    public string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>Opens the download of one model; the tests hand in a stream of their own.</summary>
    internal Func<GgmlType, CancellationToken, Task<Stream>> OpenDownload { get; init; } =
        (ggml, ct) => WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggml, cancellationToken: ct);

    /// <summary>Without a known length, a download this close to the model's size counts as whole.
    /// The sizes in <see cref="Info"/> are within a fraction of a percent of the real files.</summary>
    internal const double CompleteShare = 0.99;

    /// <summary>Everything the library needs to know about one model. <see cref="ApproximateBytes"/>
    /// scales the progress bar and tells a whole download from a cut one: the download stream does
    /// not expose a content length.</summary>
    private readonly record struct ModelInfo(string FileName, long ApproximateBytes, GgmlType Ggml);

    /// <summary>One switch, not three. Split across three, a model added to the enum and wired
    /// into two of them compiles, passes a test that walks the public two, and then throws on the
    /// download out of the third. Here a forgotten model fails every caller at once, so walking the
    /// enum really does guard the download path.</summary>
    private static ModelInfo Info(WhisperModel model) => model switch
    {
        WhisperModel.TinyEnglish => new("ggml-tiny.en.bin", 77_700_000, GgmlType.TinyEn),
        WhisperModel.BaseEnglish => new("ggml-base.en.bin", 148_000_000, GgmlType.BaseEn),
        WhisperModel.SmallEnglish => new("ggml-small.en.bin", 488_000_000, GgmlType.SmallEn),
        WhisperModel.LargeV3Turbo => new("ggml-large-v3-turbo.bin", 1_624_000_000, GgmlType.LargeV3Turbo),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
    };

    public static string FileName(WhisperModel model) => Info(model).FileName;

    public static long ApproximateBytes(WhisperModel model) => Info(model).ApproximateBytes;

    /// <summary>Downloads the model in use now; joins its download if one runs. <paramref name="ct"/> stops the wait,
    /// not the download: another caller may be waiting for it too.</summary>
    public Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Downloading download;
        lock (_lock)
        {
            if (!_downloads.TryGetValue(_model, out var running))
            {
                running = new Downloading(_model, new ByteProgress(0, ApproximateBytes(_model)));
                running.Task = Task.Run(() => DownloadOnPoolAsync(running));
                _downloads[_model] = running;
            }

            download = running;
            if (progress is not null)
            {
                download.Listeners.Add(progress);
            }
        }

        DownloadChanged?.Invoke(this, EventArgs.Empty);
        return download.Task!.WaitAsync(ct);
    }

    private async Task DownloadOnPoolAsync(Downloading download)
    {
        try
        {
            await DownloadFileAsync(download.Model, fraction => Tell(download, fraction)).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _downloads.Remove(download.Model);
            }

            DownloadChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Tell(Downloading download, double fraction)
    {
        IProgress<double>[] listeners;
        bool changed;
        lock (_lock)
        {
            listeners = [.. download.Listeners];
            var bytes = new ByteProgress((long)(fraction * download.Bytes.Total), download.Bytes.Total);
            // Once a megabyte, and only of the model in use: 1.6 GB read 80 KB at a time would tell twenty thousand times.
            changed = bytes.Done / 1_000_000 != download.Bytes.Done / 1_000_000 && download.Model == _model;
            download.Bytes = bytes;
        }

        foreach (var listener in listeners)
        {
            listener.Report(fraction);
        }

        if (changed)
        {
            DownloadChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task DownloadFileAsync(WhisperModel model, Action<double> progress)
    {
        Directory.CreateDirectory(options.Value.ModelFolder);
        var path = PathOf(model);
        var partial = path + ".partial";
        var approximate = ApproximateBytes(model);
        try
        {
            using var source = await OpenDownload(Info(model).Ggml, CancellationToken.None).ConfigureAwait(false);
            // A seekable stream knows exactly how much is to come; the HTTP stream does not.
            long? exact = source.CanSeek ? source.Length - source.Position : null;
            var expected = (double)(exact ?? approximate);
            // ConfigureAwait(false) throughout: 1.6 GB in 80 KB reads is some twenty thousand
            // continuations, none of which needs the caller's thread. Progress is marshalled by
            // whoever reports it.
            long read = 0;
            var target = File.Create(partial);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[81_920];
                int n;
                while ((n = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                    read += n;
                    progress(Math.Min(read / expected, 0.99));
                }
            }

            // Short: the finally deletes the .partial, and the caller says the download failed.
            if (exact is { } length ? read != length : read < approximate * CompleteShare)
            {
                var of = exact is { } whole ? $"{whole:N0}" : $"about {approximate:N0}";
                throw new IOException($"the download ended early, after {read:N0} of {of} bytes");
            }

            File.Move(partial, path, overwrite: true);
            progress(1.0);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial); // no-op after a successful Move
            }
        }
    }

    /// <summary>A download in flight, and who is told of it.</summary>
    private sealed class Downloading(WhisperModel model, ByteProgress bytes)
    {
        public WhisperModel Model { get; } = model;

        public ByteProgress Bytes { get; set; } = bytes;

        public Task? Task { get; set; }

        public List<IProgress<double>> Listeners { get; } = [];
    }
}
