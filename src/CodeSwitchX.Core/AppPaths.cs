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

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
