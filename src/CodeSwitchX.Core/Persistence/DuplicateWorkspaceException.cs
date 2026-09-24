namespace CodeSwitchX.Core.Persistence;

public sealed class DuplicateWorkspaceException(string rootPath)
    : InvalidOperationException($"A workspace with root '{rootPath}' is already registered.")
{
    public string RootPath { get; } = rootPath;
}
