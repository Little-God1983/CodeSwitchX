using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode;

public readonly record struct LaunchResult(bool Started, int? ProcessId, string? Error);

public interface IVsCodeLauncher
{
    LaunchResult Launch(Workspace workspace);

    /// <summary>Hands a <c>vscode://</c> link to VS Code, which gives it to its window focused last; null once handed over, else why not.</summary>
    string? OpenUrl(string url);
}
