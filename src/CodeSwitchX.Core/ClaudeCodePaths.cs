namespace CodeSwitchX.Core;

/// <summary>Where Claude Code keeps its user-level settings and transcripts.</summary>
public sealed class ClaudeCodePaths
{
    private readonly string? _claudeDirectory;

    /// <param name="claudeDirectory">Claude Code's config folder when it is not <c>~\.claude</c> (<c>CLAUDE_CONFIG_DIR</c>).</param>
    public ClaudeCodePaths(string homeDirectory, string? claudeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        HomeDirectory = homeDirectory;
        _claudeDirectory = string.IsNullOrWhiteSpace(claudeDirectory) ? null : claudeDirectory;
    }

    /// <summary>The folders Claude Code itself uses: <c>CLAUDE_CONFIG_DIR</c> when set, otherwise <c>~\.claude</c>.</summary>
    public static ClaudeCodePaths Default() =>
        Resolve(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

    internal static ClaudeCodePaths Resolve(string homeDirectory, string? configDirectory) => new(homeDirectory, configDirectory);

    public string HomeDirectory { get; }
    public string ClaudeDirectory => _claudeDirectory ?? Path.Combine(HomeDirectory, ".claude");
    public string SettingsFile => Path.Combine(ClaudeDirectory, "settings.json");
    public string ProjectsDirectory => Path.Combine(ClaudeDirectory, "projects");
}
