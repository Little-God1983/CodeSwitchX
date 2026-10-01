namespace CodeSwitchX.Core.Workspaces;

/// <summary>The folders VS Code opens for a workspace: its root for a folder, the listed folders for a <c>.code-workspace</c>.</summary>
public sealed record WorkspaceWindow(Guid WorkspaceId, IReadOnlyList<string> Folders);
