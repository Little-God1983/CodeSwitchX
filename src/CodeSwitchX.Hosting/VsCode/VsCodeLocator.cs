namespace CodeSwitchX.Hosting.VsCode;

public static class VsCodeLocator
{
    public const string OverrideVariable = "CODESWITCHX_VSCODE_EXE";

    public static string? FindExecutable() => FindExecutable(File.Exists, Environment.GetEnvironmentVariable);

    public static string? FindExecutable(Func<string, bool> fileExists, Func<string, string?> env)
    {
        if (env(OverrideVariable) is { Length: > 0 } explicitPath && fileExists(explicitPath))
        {
            return explicitPath;
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
            if (fileExists(Path.Combine(dir, "code.cmd")))
            {
                var sibling = Path.GetFullPath(Path.Combine(dir, "..", "Code.exe"));
                if (fileExists(sibling))
                {
                    return sibling;
                }
            }
        }

        return null;
    }
}
