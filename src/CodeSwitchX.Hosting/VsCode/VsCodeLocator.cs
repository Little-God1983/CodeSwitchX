namespace CodeSwitchX.Hosting.VsCode;

public static class VsCodeLocator
{
    public const string OverrideVariable = "CODESWITCHX_VSCODE_EXE";

    public static string? FindExecutable() => FindExecutable(File.Exists, Environment.GetEnvironmentVariable);

    public static string? FindExecutable(Func<string, bool> fileExists, Func<string, string?> env)
    {
        if (env(OverrideVariable) is { Length: > 0 } explicitPath && fileExists(explicitPath))
        {
            // The CLI shim (bin\code.cmd) is a common override; it launches the Code.exe next to it, which is what is matched.
            return CodeExeBesideShim(explicitPath, fileExists) ?? explicitPath;
        }

        foreach (var root in new[] { env("LOCALAPPDATA") is { Length: > 0 } local ? Path.Combine(local, "Programs") : null, env("ProgramFiles"), env("ProgramFiles(x86)") })
        {
            if (root is null)
            {
                continue;
            }

            var candidate = Path.Combine(root, "Microsoft VS Code", "Code.exe");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        foreach (var dir in (env("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var shim = Path.Combine(dir, "code.cmd");
            if (fileExists(shim) && CodeExeBesideShim(shim, fileExists) is { } sibling)
            {
                return sibling;
            }
        }

        return null;
    }

    /// <summary>The Code.exe one folder above a bin\code.cmd shim, when the path is that shim and the exe is there.</summary>
    private static string? CodeExeBesideShim(string path, Func<string, bool> fileExists)
    {
        if (!string.Equals(Path.GetFileName(path), "code.cmd", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(path) is not { } bin)
        {
            return null;
        }

        var sibling = Path.GetFullPath(Path.Combine(bin, "..", "Code.exe"));
        return fileExists(sibling) ? sibling : null;
    }
}
