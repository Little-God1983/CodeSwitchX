namespace CodeSwitchX.Core.Workspaces;

/// <summary>Which VS Code window a chat runs in, told by its claude process.</summary>
public interface IIdeWindows
{
    /// <summary>
    /// Looks up the folders open in the VS Code window whose Claude Code extension started the claude process
    /// <paramref name="claudePid"/>. <paramref name="ancestors"/> are the processes above it, its parent first, when
    /// they are known (a hook event carries them); null reads them from the system.
    /// </summary>
    /// <param name="folders">The window's folders; null when no VS Code window started it (a terminal, another IDE, a process that is gone).</param>
    /// <returns>False when the system could not be read: nothing is known, ask again later.</returns>
    bool TryFoldersOf(int claudePid, IReadOnlyList<int>? ancestors, out IReadOnlyList<string>? folders);
}
