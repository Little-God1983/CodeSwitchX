using CodeSwitchX.Voice.Speech.Sidecar;

namespace CodeSwitchX.Voice.Speech.Kokoro;

/// <summary>
/// Kokoro's environment, in the voice folder's "kokoro" folder, made with <see cref="Uv"/>: kokoro-onnx, which runs
/// Kokoro-82M with ONNX Runtime on the CPU and brings eSpeak NG for the pronunciation, its packages pinned by
/// <c>constraints.txt</c>. The model and its voices (about 350 MB) are downloaded by the install, from kokoro-onnx's
/// release, into the models folder; about 450 MB in all.
/// </summary>
public sealed class KokoroEnvironment : ISidecarEnvironment
{
    /// <summary>Raised whenever the recipe changes (a pin, a step): an environment stamped with another version is installed again.</summary>
    public const string RecipeVersion = "1";

    /// <summary>The one model there is; <see cref="SpeechSettings.ModelOf"/> names it.</summary>
    public const string Model = "kokoro-v1.0";

    private const string EnginePackage = "kokoro-onnx==0.6.1";
    private const string ConstraintsFile = "constraints.txt";
    private const string Release = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";

    /// <summary>The files and their sizes: a file of another size is a download cut off, and is fetched again.</summary>
    internal static readonly IReadOnlyList<(string Name, long Bytes)> ModelFiles =
    [
        ("kokoro-v1.0.onnx", 325_532_387),
        ("voices-v1.0.bin", 28_214_398),
    ];

    /// <summary>The files to fetch; the tests fetch small ones.</summary>
    internal IReadOnlyList<(string Name, long Bytes)> Files { get; init; } = ModelFiles;

    private readonly string _root;
    private readonly string _models;
    private readonly Uv _uv;
    private readonly HttpClient _http;

    /// <param name="root">Where the environment goes: the voice folder's "kokoro" folder.</param>
    /// <param name="models">Where the model files go.</param>
    public KokoroEnvironment(string root, string models, Uv uv, HttpClient http)
    {
        _root = root;
        _models = models;
        _uv = uv;
        _http = http;
    }

    private string Venv => Path.Combine(_root, "venv");

    private string Stamp => Path.Combine(_root, "installed.txt");

    public string Python => Path.Combine(Venv, "Scripts", "python.exe");

    public IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>();

    /// <summary>The environment and the model; with the model only short, an install fetches just the missing files.</summary>
    public bool IsInstalled => HasEnvironment && HasModel(Model);

    /// <summary>The Python environment, installed completely by this version of the recipe.</summary>
    private bool HasEnvironment => File.Exists(Python) && File.Exists(Stamp) && File.ReadAllText(Stamp).Trim() == RecipeVersion;

    public bool HasModel(string model) => Files.All(f => SizeOf(Path.Combine(_models, f.Name)) == f.Bytes);

    /// <summary>The folder holding the model files.</summary>
    public string ModelArgument(string model) => _models;

    public string WriteScript() => SidecarScripts.Write(_root, "Kokoro.kokoro_tts_server.py");

    /// <summary>Makes the environment unless it is whole, then fetches the model files not on disk whole.</summary>
    public async Task InstallAsync(IProgress<InstallStep> progress, CancellationToken ct)
    {
        if (!HasEnvironment)
        {
            await InstallEnvironmentAsync(progress, ct).ConfigureAwait(false);
        }

        await DownloadModelAsync(progress, ct).ConfigureAwait(false);
    }

    private async Task InstallEnvironmentAsync(IProgress<InstallStep> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        File.Delete(Stamp);
        if (Directory.Exists(Venv))
        {
            progress.Report(new("removing the old install"));
            Directory.Delete(Venv, recursive: true);
        }

        var uv = await _uv.FindAsync(progress, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, ConstraintsFile), SidecarScripts.Resource("Kokoro." + ConstraintsFile), ct).ConfigureAwait(false);

        progress.Report(new("getting Python " + Uv.PythonVersion));
        await _uv.CreateVenvAsync(uv, _root, Venv, ct).ConfigureAwait(false);

        progress.Report(new("downloading Kokoro"));
        // By name, from the environment's folder (uv runs there): uv 0.11 cuts a constraints path at its first space.
        await _uv.RunAsync(uv, _root, ["pip", "install", "--python", Python, EnginePackage, "--constraint", ConstraintsFile], ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(Stamp, RecipeVersion, ct).ConfigureAwait(false);
    }

    /// <summary>The files not on disk whole yet, one after the other, told as one download; each into a ".partial" file first.</summary>
    private async Task DownloadModelAsync(IProgress<InstallStep> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_models);
        var missing = Files.Where(f => SizeOf(Path.Combine(_models, f.Name)) != f.Bytes).ToList();
        var total = missing.Sum(f => f.Bytes);
        long done = 0;
        var told = -1L;
        void Tell()
        {
            // Once a megabyte: a 330 MB download read 80 KB at a time would tell four thousand times.
            if (done / 1_000_000 != told)
            {
                told = done / 1_000_000;
                progress.Report(new("downloading the model", new ByteProgress(done, total)));
            }
        }

        Tell();
        foreach (var (name, bytes) in missing)
        {
            var path = Path.Combine(_models, name);
            var partial = path + ".partial";
            try
            {
                using var response = await _http.GetAsync(Release + name, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var target = File.Create(partial);
                    await using (target.ConfigureAwait(false))
                    {
                        var buffer = new byte[81_920];
                        int n;
                        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            done += n;
                            Tell();
                        }
                    }
                }

                if (SizeOf(partial) != bytes)
                {
                    throw new TextToSpeechException($"The download of {name} ended early, after {SizeOf(partial):N0} of {bytes:N0} bytes.");
                }

                File.Move(partial, path, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new TextToSpeechException($"Kokoro's model could not be downloaded: {ex.Message}", ex);
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
