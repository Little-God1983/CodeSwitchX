using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Messaging;

public sealed record HookEventReceived(HookEvent Event);

public sealed record TranscriptUpdated(TranscriptUpdate Update);

public sealed record SessionChanged(SessionSnapshot? Previous, SessionSnapshot Current);

public sealed record WorkspaceRegistered(Workspace Workspace);

public sealed record WorkspaceUnregistered(Guid WorkspaceId);

/// <summary>Raised after the resolver's roots were replaced; consumers re-map anything keyed by path.</summary>
public sealed record WorkspaceRootsChanged;
