using System.Diagnostics;
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

    /// <summary>
    /// The arguments one by one: <see cref="ProcessStartInfo.ArgumentList"/> quotes each for Windows' parser, where a
    /// backslash before a closing quote would otherwise escape it (a drive root "R:\" arrived as R:").
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(Workspace workspace) =>
        string.IsNullOrWhiteSpace(workspace.VsCodeProfile)
            ? ["--new-window", Target(workspace)]
            : ["--new-window", "--profile", workspace.VsCodeProfile, Target(workspace)];

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
            var info = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Directory.Exists(workspace.RootPath) ? workspace.RootPath : null,
            };
            foreach (var argument in BuildArguments(workspace))
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            return new LaunchResult(true, process?.Id, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new LaunchResult(false, null, ex.Message);
        }
    }

    private static string Target(Workspace workspace) => workspace.WorkspaceFile is { Length: > 0 } file ? file : workspace.RootPath;
}
