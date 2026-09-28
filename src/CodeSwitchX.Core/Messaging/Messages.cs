using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Messaging;

public sealed record HookEventReceived(HookEvent Event);

public sealed record TranscriptUpdated(TranscriptUpdate Update);

/// <summary>
/// Transcripts a scan no longer finds (Claude Code's cleanup deleted them): their cursors are to be removed, in order with
/// the cursors still queued, so a cursor queued before the file went is not written after its row was removed.
/// </summary>
public sealed record TranscriptsForgotten(IReadOnlyList<string> Paths);

public sealed record SessionChanged(SessionSnapshot? Previous, SessionSnapshot Current);

public sealed record WorkspaceRegistered(Workspace Workspace);

public sealed record WorkspaceUnregistered(Guid WorkspaceId);

/// <summary>Raised after the resolver's roots were replaced; consumers re-map anything keyed by path.</summary>
public sealed record WorkspaceRootsChanged;
