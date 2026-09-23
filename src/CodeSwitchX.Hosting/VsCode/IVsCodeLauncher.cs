using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode;

public readonly record struct LaunchResult(bool Started, int? ProcessId, string? Error);

public interface IVsCodeLauncher
{
    LaunchResult Launch(Workspace workspace);
}
