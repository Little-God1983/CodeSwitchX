using CodeSwitchX.Core.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Data;

public sealed class DatabaseInitializer
{
    public const string DefaultTrackName = "General";

    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IDbContextFactory<CodeSwitchXDbContext> factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // MigrateAsync takes EF Core's migration lock even when nothing is pending, so it only runs for an upgrade.
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            _logger.LogInformation("Applying {Count} database migrations", pending.Count);
            await ReleaseLeftoverMigrationLockAsync(db, ct);
            await db.Database.MigrateAsync(ct);
        }

        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        if (!await db.Tracks.AnyAsync(ct))
        {
            db.Tracks.Add(new Track { Name = DefaultTrackName, SortOrder = 0 });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// EF Core's SQLite migration lock is a row in <c>__EFMigrationsLock</c> that it deletes when done, and it waits for
    /// that row without a timeout. A start killed while migrating leaves the row behind, and every later start would hang
    /// with no window and no error. Only CodeSwitchX migrates this file, and only at startup, so a row found here is left
    /// over unless a second instance is upgrading at the same moment.
    /// </summary>
    private async Task ReleaseLeftoverMigrationLockAsync(CodeSwitchXDbContext db, CancellationToken ct)
    {
        var lockTableExists = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsLock'")
            .SingleAsync(ct) > 0;
        if (lockTableExists && await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsLock\";", ct) > 0)
        {
            _logger.LogWarning("Removed a database migration lock left behind by an earlier start that did not finish");
        }
    }
}
