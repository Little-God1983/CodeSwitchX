using Microsoft.Extensions.Options;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace CodeSwitchX.Voice.Dictation;

/// <summary>Where the model file lives and how it gets there. The download goes to a
/// ".partial" file and is renamed only when complete, so a half file is never mistaken for a
/// model: IsPresent looks at the final name only.</summary>
public sealed class WhisperModelStore(IOptions<DictationOptions> options) : IWhisperModelStore
{
    public WhisperModel Model => options.Value.Model;

    public string ModelPath => Path.Combine(options.Value.ModelFolder, FileName(Model));

    public bool IsPresent => File.Exists(ModelPath);

    // One look at the file: FileInfo caches what Exists read, so Length reports that same file. A
    // separate File.Exists and then FileInfo.Length could see a file that went away in between
    // (a delete, or a download's rename swapping it) and throw FileNotFoundException.
    public long? SizeBytes => new FileInfo(ModelPath) is { Exists: true } file ? file.Length : null;

    // Whisper.net picks a native backend the first time a WhisperFactory is built and remembers
    // it process-wide, so this is null until someone has dictated once.
    public string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>Everything the library needs to know about one model. <see cref="ApproximateBytes"/>
    /// only scales the progress bar; the download stream does not expose a content length.</summary>
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

    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(options.Value.ModelFolder);
        var partial = ModelPath + ".partial";
        var expected = (double)ApproximateBytes(Model);
        try
        {
            using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(Info(Model).Ggml, cancellationToken: ct);
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81_920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    progress?.Report(Math.Min(read / expected, 0.99));
                }
            }

            File.Move(partial, ModelPath, overwrite: true);
            progress?.Report(1.0);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial); // no-op after a successful Move
            }
        }
    }
}
