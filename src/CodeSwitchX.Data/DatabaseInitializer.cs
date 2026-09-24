using System.Diagnostics;
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

    /// <summary>How long an upgrade waits for another instance's migration lock before it treats the lock as left over.</summary>
    internal TimeSpan MigrationLockWait { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(250);

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
        else if (db.Database.HasPendingModelChanges())
        {
            // MigrateAsync would refuse this model, so an up-to-date database needs the same check before the first write fails.
            throw new InvalidOperationException("The data model has changes that no migration covers; add one with 'dotnet ef migrations add'.");
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
    /// with no window and no error. A second instance that is upgrading holds the row only while its migration runs, so a
    /// row still there after <see cref="MigrationLockWait"/> is left over.
    /// </summary>
    private async Task ReleaseLeftoverMigrationLockAsync(CodeSwitchXDbContext db, CancellationToken ct)
    {
        if (await CountAsync(db, "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsLock'", ct) == 0)
        {
            return;
        }

        var waited = Stopwatch.StartNew();
        while (await CountAsync(db, "SELECT COUNT(*) AS \"Value\" FROM \"__EFMigrationsLock\"", ct) > 0)
        {
            if (waited.Elapsed >= MigrationLockWait)
            {
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsLock\";", ct);
                _logger.LogWarning("Removed a database migration lock left behind by an earlier start that did not finish");
                return;
            }

            await Task.Delay(LockPollInterval, ct);
        }
    }

    private static Task<int> CountAsync(CodeSwitchXDbContext db, string sql, CancellationToken ct) => db.Database.SqlQueryRaw<int>(sql).SingleAsync(ct);
}
