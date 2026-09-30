namespace CodeSwitchX.Core;

/// <summary>Layout of the per-user data folder (%LOCALAPPDATA%\CodeSwitchX by default).</summary>
public sealed class AppPaths
{
    public const string ProductFolderName = "CodeSwitchX";

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName));

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "codeswitchx.db");
    public string TokenFile => Path.Combine(Root, "token");
    public string EndpointFile => Path.Combine(Root, "endpoint.json");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string ModelsDirectory => Path.Combine(Root, "models");

    /// <summary>Where Claude Code finds CodeSwitchX's MCP server: its address and the token. Readable by the current user only.</summary>
    public string McpConfigFile => Path.Combine(Root, "mcp.json");

    /// <summary>The working folder of Raven's brain: no repository, so it has no code to look at.</summary>
    public string RavenDirectory => Path.Combine(Root, "raven");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
    }
}
