using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodeSwitchX.Data.Tests;

public class DatabaseInitializerTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task A_start_finishes_when_a_killed_earlier_start_left_the_migration_lock_behind()
    {
        await _db.InitializeAsync();
        await LeaveMigrationLockAsync();

        await InitializeWithinTenSecondsAsync();
    }

    [Fact]
    public async Task An_upgrade_finishes_when_a_killed_earlier_upgrade_left_the_migration_lock_behind()
    {
        await using (var db = await CreateContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260923184305_InitialCreate", TestContext.Current.CancellationToken);
        }

        await LeaveMigrationLockAsync();

        await InitializeWithinTenSecondsAsync();

        await using var migrated = await CreateContextAsync();
        (await migrated.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    private async Task<CodeSwitchXDbContext> CreateContextAsync() =>
        await _db.Get<IDbContextFactory<CodeSwitchXDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);

    /// <summary>The row EF Core's SQLite migration lock holds while it migrates; a start killed in between leaves it behind.</summary>
    private async Task LeaveMigrationLockAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_db.DatabaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """INSERT INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, '2026-09-24 10:00:00+00:00');""";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task InitializeWithinTenSecondsAsync()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            // EF Core waits for its migration lock without a timeout, so the test sets one.
            await _db.Get<DatabaseInitializer>().InitializeAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
        }
    }
}
