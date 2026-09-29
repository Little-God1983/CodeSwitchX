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

        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddPooledDbContextFactory<CodeSwitchXDbContext>((sp, options) => options
            // A collection in a query goes as one JSON parameter: EF Core 10 sends one parameter per item by default, and
            // SQLite takes at most 32,766 of them in a statement.
            .UseSqlite(connectionString, sqlite => sqlite.UseParameterizedCollectionMode(ParameterTranslationMode.Parameter))
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()));
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
