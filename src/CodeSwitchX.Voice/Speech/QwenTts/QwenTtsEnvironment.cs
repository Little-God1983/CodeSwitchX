using CodeSwitchX.Voice.Speech.Sidecar;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Speech.QwenTts;

/// <summary>
/// Qwen3-TTS's environment, directly in the voice folder, made with <see cref="Uv"/>: PyTorch for CUDA 12.8 (the build
/// that runs on Blackwell cards) and faster-qwen3-tts, whose CUDA graphs make Qwen3-TTS several times faster than real
/// time on Windows (the plain qwen-tts package is three times slower than real time there). Every other package is pinned
/// by <c>constraints.txt</c>, frozen from an environment that ran. The models are downloaded by the sidecar as it first
/// starts one, into Hugging Face's cache under <see cref="ModelCache"/>: about 5 GB in all with the 0.6B model.
/// </summary>
public sealed class QwenTtsEnvironment : ISidecarEnvironment
{
    /// <summary>Raised whenever the recipe changes (a pin, a step): an environment stamped with another version is installed again.</summary>
    public const string RecipeVersion = "1";

    private const string TorchIndex = "https://download.pytorch.org/whl/cu128";
    private static readonly string[] TorchPackages = ["torch==2.11.0", "torchaudio==2.11.0"];
    private const string EnginePackage = "faster-qwen3-tts==0.5.3";
    private const string ConstraintsFile = "constraints.txt";

    private readonly string _root;
    private readonly Uv _uv;

    /// <param name="root">The voice folder: Qwen3-TTS's environment goes in here.</param>
    public QwenTtsEnvironment(string root, string modelCache, Uv uv)
    {
        _root = root;
        ModelCache = modelCache;
        _uv = uv;
        Variables = new Dictionary<string, string> { ["HF_HOME"] = modelCache, ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1" };
    }

    private string Venv => Path.Combine(_root, "venv");

    private string Stamp => Path.Combine(_root, "installed.txt");

    public string Python => Path.Combine(Venv, "Scripts", "python.exe");

    /// <summary>Hugging Face's home: the models are downloaded to and loaded from its cache.</summary>
    public string ModelCache { get; }

    public IReadOnlyDictionary<string, string> Variables { get; }

    public bool IsInstalled =>
        File.Exists(Python) && File.Exists(Stamp) && File.ReadAllText(Stamp).Trim() == RecipeVersion;

    /// <summary>
    /// A snapshot of the model in Hugging Face's cache with its weights, and no file still downloading: a download cut off
    /// leaves an ".incomplete" blob, which the next start finishes.
    /// </summary>
    public bool HasModel(string model)
    {
        var folder = Path.Combine(ModelCache, "hub", "models--" + model.Replace("/", "--", StringComparison.Ordinal));
        try
        {
            var snapshots = Path.Combine(folder, "snapshots");
            return Directory.Exists(snapshots)
                && Directory.EnumerateDirectories(snapshots).Any(s => File.Exists(Path.Combine(s, "model.safetensors")))
                && !(Directory.Exists(Path.Combine(folder, "blobs")) && Directory.EnumerateFiles(Path.Combine(folder, "blobs"), "*.incomplete").Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The Hugging Face id.</summary>
    public string ModelArgument(string model) => model;

    public string WriteScript() => SidecarScripts.Write(_root, "QwenTts.qwen_tts_server.py");

    public async Task InstallAsync(IProgress<InstallStep> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        File.Delete(Stamp);
        if (Directory.Exists(Venv))
        {
            progress.Report(new("removing the old install"));
            Directory.Delete(Venv, recursive: true);
        }

        var uv = await _uv.FindAsync(progress, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, ConstraintsFile), SidecarScripts.Resource("QwenTts." + ConstraintsFile), ct).ConfigureAwait(false);

        progress.Report(new("getting Python " + Uv.PythonVersion));
        await _uv.CreateVenvAsync(uv, _root, Venv, ct).ConfigureAwait(false);

        progress.Report(new("downloading PyTorch (2.5 GB)"));
        await _uv.RunAsync(uv, _root, ["pip", "install", "--python", Python, .. TorchPackages, "--index-url", TorchIndex], ct).ConfigureAwait(false);

        progress.Report(new("downloading Qwen3-TTS"));
        // By name, from the voice folder (uv runs there): uv 0.11 cuts a constraints path at its first space, and the
        // data folder is under the user's profile ("C:/Users/Little God/...").
        await _uv.RunAsync(uv, _root, ["pip", "install", "--python", Python, EnginePackage, "--constraint", ConstraintsFile], ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(Stamp, RecipeVersion, ct).ConfigureAwait(false);
    }
}
