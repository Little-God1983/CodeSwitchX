using CodeSwitchX.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

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
        await MigrateToInitialCreateAsync();
        await LeaveMigrationLockAsync();
        _db.Get<DatabaseInitializer>().MigrationLockWait = TimeSpan.FromSeconds(1);

        await InitializeWithinTenSecondsAsync();

        await using var migrated = await CreateContextAsync();
        (await migrated.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_upgrade_waits_for_the_migration_lock_of_another_instance_that_is_still_upgrading()
    {
        await MigrateToInitialCreateAsync();
        await LeaveMigrationLockAsync();

        var initializing = _db.Get<DatabaseInitializer>().InitializeAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        (await ExecuteAsync("""SELECT COUNT(*) FROM "__EFMigrationsLock";""")).ShouldBe(1L, "the other instance keeps its lock");
        initializing.IsCompleted.ShouldBeFalse();

        await ExecuteAsync("""DELETE FROM "__EFMigrationsLock";""");
        await initializing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_upgrade_removes_the_default_prices_that_earlier_starts_copied_into_the_database()
    {
        await MigrateToAsync("20260923223337_SeenMessages");
        await ExecuteAsync("""INSERT INTO "PricingRules" ("Model", "InputPerM", "OutputPerM", "CacheWritePerM", "CacheReadPerM", "ContextWindow") VALUES ('claude-sonnet-5', 3, 15, 3.75, 0.3, 200000);""");

        await InitializeWithinTenSecondsAsync();

        // A stored rule overrides the shipped one for its model, and nothing has written a rule of the user's own yet.
        (await ExecuteAsync("""SELECT COUNT(*) FROM "PricingRules";""")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_start_fails_when_the_model_has_changes_without_a_migration()
    {
        await _db.InitializeAsync();
        var options = new DbContextOptionsBuilder<CodeSwitchXDbContext>()
            .UseSqlite($"Data Source={_db.DatabaseFile}")
            .ReplaceService<IModelCustomizer, ForgottenMigration>()
            .Options;
        var initializer = new DatabaseInitializer(new PooledDbContextFactory<CodeSwitchXDbContext>(options), NullLogger<DatabaseInitializer>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => initializer.InitializeAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A model change that nobody added a migration for.</summary>
    private sealed class ForgottenMigration(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<Setting>().Property<string>("Forgotten");
        }
    }

    private async Task<CodeSwitchXDbContext> CreateContextAsync() =>
        await _db.Get<IDbContextFactory<CodeSwitchXDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);

    private Task MigrateToInitialCreateAsync() => MigrateToAsync("20260923184305_InitialCreate");

    private async Task MigrateToAsync(string migration)
    {
        await using var db = await CreateContextAsync();
        await db.GetService<IMigrator>().MigrateAsync(migration, TestContext.Current.CancellationToken);
    }

    /// <summary>The row EF Core's SQLite migration lock holds while it migrates; a start killed in between leaves it behind.</summary>
    private Task LeaveMigrationLockAsync() =>
        ExecuteAsync("""INSERT INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, '2026-09-24 10:00:00+00:00');""");

    /// <summary>Runs SQL on a connection of its own, the way another CodeSwitchX process would.</summary>
    private async Task<object?> ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_db.DatabaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
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
