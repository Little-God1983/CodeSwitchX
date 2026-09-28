using System.Diagnostics;
using System.Text;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode;

public sealed class VsCodeLauncher : IVsCodeLauncher
{
    private readonly string? _executable;

    public VsCodeLauncher(string? executable = null)
    {
        _executable = executable ?? VsCodeLocator.FindExecutable();
    }

    public string? Executable => _executable;

    public static string BuildArguments(Workspace workspace)
    {
        var profile = string.IsNullOrWhiteSpace(workspace.VsCodeProfile) ? string.Empty : $" --profile {Quote(workspace.VsCodeProfile)}";
        return $"--new-window{profile} {Quote(Target(workspace))}";
    }

    /// <summary>The text VS Code puts in its title for this target: the folder name, or "<file> (Workspace)".</summary>
    public static string DisplayNameForMatching(Workspace workspace)
    {
        if (workspace.WorkspaceFile is { Length: > 0 } file)
        {
            return $"{Path.GetFileNameWithoutExtension(file)} (Workspace)";
        }

        // A drive root has no folder name: VS Code shows the drive, the base name of its /R:/ path.
        var root = workspace.RootPath.TrimEnd('\\', '/');
        var name = Path.GetFileName(root);
        return name.Length > 0 ? name : root;
    }

    public LaunchResult Launch(Workspace workspace)
    {
        // VS Code opens a missing command-line path as a new, unsaved file. Its tab puts the folder's name in the title,
        // so the window would pass for the workspace, and saving it would write a file where the folder was.
        var target = Target(workspace);
        if (workspace.WorkspaceFile is { Length: > 0 } ? !File.Exists(target) : !Directory.Exists(target))
        {
            return new LaunchResult(false, null, $"{target} was not found. It may have been moved or renamed, or its drive is not connected.");
        }

        if (_executable is null || !File.Exists(_executable))
        {
            return new LaunchResult(false, null, $"VS Code executable not found ({_executable ?? "no candidate"}). Set {VsCodeLocator.OverrideVariable}.");
        }

        try
        {
            var info = new ProcessStartInfo(_executable, BuildArguments(workspace))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Directory.Exists(workspace.RootPath) ? workspace.RootPath : null,
            };
            using var process = Process.Start(info);
            return new LaunchResult(true, process?.Id, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new LaunchResult(false, null, ex.Message);
        }
    }

    private static string Target(Workspace workspace) => workspace.WorkspaceFile is { Length: > 0 } file ? file : workspace.RootPath;

    /// <summary>
    /// Quotes one argument so that Windows' command-line parsing gives it back unchanged. Backslashes count only in
    /// front of a quote, the closing one included: there each is doubled. A drive root "R:\" would otherwise arrive as R:".
    /// </summary>
    private static string Quote(string argument)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
