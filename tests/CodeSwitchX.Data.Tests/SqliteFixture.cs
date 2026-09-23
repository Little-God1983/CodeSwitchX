using CodeSwitchX.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Data.Tests;

/// <summary>A fresh migrated SQLite file per test class instance, deleted on dispose.</summary>
public sealed class SqliteFixture : IAsyncDisposable
{
    public SqliteFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "csx-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DatabaseFile = Path.Combine(Root, "test.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeSwitchXData(DatabaseFile);
        Services = services.BuildServiceProvider();
    }

    public string Root { get; }
    public string DatabaseFile { get; }
    public ServiceProvider Services { get; }

    public async Task InitializeAsync()
    {
        await Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort; the temp folder is cleaned by the OS eventually
        }
    }
}
