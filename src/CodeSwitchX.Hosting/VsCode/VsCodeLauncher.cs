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

    public static string BuildArguments(Workspace workspace)
    {
        var target = workspace.WorkspaceFile ?? workspace.RootPath;
        var profile = string.IsNullOrWhiteSpace(workspace.VsCodeProfile) ? string.Empty : $" --profile \"{workspace.VsCodeProfile}\"";
        return $"--new-window{profile} \"{target}\"";
    }

    /// <summary>The text VS Code puts in its title for this target: the folder name, or "<file> (Workspace)".</summary>
    public static string DisplayNameForMatching(Workspace workspace) =>
        workspace.WorkspaceFile is { Length: > 0 } file
            ? $"{Path.GetFileNameWithoutExtension(file)} (Workspace)"
            : Path.GetFileName(workspace.RootPath.TrimEnd('\\', '/'));

    public LaunchResult Launch(Workspace workspace)
    {
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
}
