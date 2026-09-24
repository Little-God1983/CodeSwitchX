namespace CodeSwitchX.Core.Workspaces;

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
