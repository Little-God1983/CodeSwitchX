namespace CodeSwitchX.UI.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    /// <summary>Tests that change the process's current directory, which every other test would see.</summary>
    public const string Name = "Current directory";
}

[Collection(CurrentDirectoryCollection.Name)]
public class AppHostTests
{
    [Fact]
    public void A_malformed_appsettings_json_in_the_folder_CodeSwitchX_is_started_from_does_not_stop_the_start()
    {
        var folder = Directory.CreateTempSubdirectory("csx-cwd-").FullName;
        File.WriteAllText(Path.Combine(folder, "appsettings.json"), """{ "Logging": """);
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = folder;
        try
        {
            using var host = App.CreateHostBuilder().Build();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(folder, recursive: true);
        }
    }
}
