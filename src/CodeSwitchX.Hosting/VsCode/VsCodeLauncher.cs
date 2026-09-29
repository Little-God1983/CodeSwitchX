using System.Diagnostics;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode;

public sealed class VsCodeLauncher : IVsCodeLauncher
{
    private readonly Func<string?> _locate;
    private string? _executable;

    /// <param name="executable">A fixed path; without one VS Code is located when it is needed (see <see cref="Executable"/>).</param>
    public VsCodeLauncher(string? executable = null)
        : this(executable is null ? VsCodeLocator.FindExecutable : () => executable)
    {
    }

    public VsCodeLauncher(Func<string?> locate)
    {
        _locate = locate;
    }

    /// <summary>
    /// The VS Code executable, located again when it was not found before or the path found then is gone: this launcher
    /// lives as long as CodeSwitchX, and VS Code may be installed or moved meanwhile.
    /// </summary>
    public string? Executable
    {
        get
        {
            if (_executable is null || !File.Exists(_executable))
            {
                _executable = _locate();
            }

            return _executable;
        }
    }

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

        var executable = Executable;
        if (executable is null || !File.Exists(executable))
        {
            return new LaunchResult(false, null, $"VS Code executable not found ({executable ?? "no candidate"}). Set {VsCodeLocator.OverrideVariable}.");
        }

        // Windows are found by the process name Code and a title that says Visual Studio Code: another build (Insiders,
        // VSCodium) would launch, and then every open would time out without a word about why.
        if (!string.Equals(Path.GetFileName(executable), VsCodeWindowMatcher.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchResult(false, null, $"{executable} is not Code.exe. CodeSwitchX finds VS Code windows by the process name Code and a title that says Visual Studio Code, so Insiders and VSCodium builds are not supported yet.");
        }

        try
        {
            var info = new ProcessStartInfo(executable)
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
