using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode;

public sealed class VsCodeLauncher : IVsCodeLauncher
{
    private readonly Func<string?> _locate;
    private string? _executable;

    /// <param name="locate">Where VS Code is; <see cref="VsCodeLocator.FindExecutable()"/> by default, a fixed path in tests.</param>
    public VsCodeLauncher(Func<string?>? locate = null)
    {
        _locate = locate ?? VsCodeLocator.FindExecutable;
    }

    /// <summary>
    /// The VS Code executable, checked on disk: located again when it was not found before or the path found then is gone,
    /// since this launcher lives as long as CodeSwitchX and VS Code may be installed or moved meanwhile.
    /// </summary>
    private bool TryLocate([NotNullWhen(true)] out string? executable)
    {
        if (_executable is not null && File.Exists(_executable))
        {
            executable = _executable;
            return true;
        }

        _executable = _locate();
        executable = _executable;
        return executable is not null && File.Exists(executable);
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

        if (!TryLocate(out var executable))
        {
            return new LaunchResult(false, null, $"VS Code executable not found ({executable ?? "no candidate"}). Set {VsCodeLocator.OverrideVariable}.");
        }

        // Windows are found by the process name Code and a title that says Visual Studio Code: another build (Insiders,
        // VSCodium) would launch, and then every open would time out without a word about why. The bin\code.cmd shim is
        // resolved to its Code.exe by the locator.
        if (!string.Equals(Path.GetFileName(executable), VsCodeWindowMatcher.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchResult(false, null, $"{executable} is not Code.exe. Point {VsCodeLocator.OverrideVariable} at Code.exe, or at the bin\\code.cmd next to it; Insiders and VSCodium builds are not supported yet.");
        }

        try
        {
            using var process = Process.Start(BuildStartInfo(executable, workspace));
            return new LaunchResult(true, process?.Id, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new LaunchResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// CodeSwitchX's own environment without ELECTRON_RUN_AS_NODE. A VS Code terminal or extension host (a Claude Code
    /// session) sets it, a CodeSwitchX started from there inherits it, and a Code.exe started with it runs as plain Node
    /// and exits with code 9, without a window.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string executable, Workspace workspace)
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

        info.Environment.Remove("ELECTRON_RUN_AS_NODE");
        return info;
    }

    private static string Target(Workspace workspace) => workspace.WorkspaceFile is { Length: > 0 } file ? file : workspace.RootPath;
}
