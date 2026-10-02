namespace CodeSwitchX.Voice.Speech.Sidecar;

/// <summary>The sidecars' Python files and pins, carried in this assembly and written next to each environment.</summary>
internal static class SidecarScripts
{
    /// <summary>The module every engine's script imports (see tts_sidecar.py).</summary>
    private const string Shared = "tts_sidecar.py";

    /// <param name="name">As the project file names it: "QwenTts.qwen_tts_server.py".</param>
    public static string Resource(string name)
    {
        using var stream = typeof(SidecarScripts).Assembly.GetManifestResourceStream("CodeSwitchX.Voice." + name)
            ?? throw new InvalidOperationException($"The resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Writes the engine's script and the shared module beside it into <paramref name="root"/>; returns the script's path.</summary>
    /// <param name="script">The engine's script, as the project file names it: "QwenTts.qwen_tts_server.py".</param>
    public static string Write(string root, string script)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Shared), Resource("Sidecar." + Shared));
        var path = Path.Combine(root, script[(script.IndexOf('.') + 1)..]);
        File.WriteAllText(path, Resource(script));
        return path;
    }
}
