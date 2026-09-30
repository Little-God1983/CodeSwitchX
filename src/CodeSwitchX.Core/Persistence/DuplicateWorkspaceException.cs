namespace CodeSwitchX.Core.Persistence;

/// <summary>The folder or <c>.code-workspace</c> file, <paramref name="target"/>, is registered as a workspace already.</summary>
public sealed class DuplicateWorkspaceException(string target)
    : InvalidOperationException($"'{target}' is already registered as a workspace.")
{
    public string Target { get; } = target;
}
