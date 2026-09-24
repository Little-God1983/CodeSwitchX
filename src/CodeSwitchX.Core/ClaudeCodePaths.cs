namespace CodeSwitchX.Core;

/// <summary>Where Claude Code keeps its user-level settings and transcripts.</summary>
public sealed class ClaudeCodePaths
{
    public ClaudeCodePaths(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        HomeDirectory = homeDirectory;
    }

    public static ClaudeCodePaths Default() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string HomeDirectory { get; }
    public string ClaudeDirectory => Path.Combine(HomeDirectory, ".claude");
    public string SettingsFile => Path.Combine(ClaudeDirectory, "settings.json");
    public string ProjectsDirectory => Path.Combine(ClaudeDirectory, "projects");
}
