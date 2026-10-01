namespace CodeSwitchX.Core.Workspaces;

/// <summary>Which VS Code window a chat runs in, told by its claude process.</summary>
public interface IIdeWindows
{
    /// <summary>
    /// The folders open in the VS Code window whose Claude Code extension started the claude process <paramref name="claudePid"/>;
    /// null when no VS Code window did (a terminal, another IDE, a process that is gone).
    /// </summary>
    IReadOnlyList<string>? FoldersOf(int claudePid);
}
