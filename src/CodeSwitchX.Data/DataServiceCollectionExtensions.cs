using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Data.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXData(this IServiceCollection services, string databaseFile)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        services.AddPooledDbContextFactory<CodeSwitchXDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IWorkspaceStore, WorkspaceStore>();
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IUsageStore, UsageStore>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<PersistenceWriterOptions>();
        services.AddSingleton<PersistenceWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());
        return services;
    }
}
