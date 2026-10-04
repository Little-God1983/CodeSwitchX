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
        var initializer = new DatabaseInitializer(new PooledDbContextFactory<CodeSwitchXDbContext>(options), new SqlitePragmaInterceptor(), NullLogger<DatabaseInitializer>.Instance);

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

    /// <summary>Workspaces stored before numbers were are numbered once, in the order the Yard shows them: by track, then by name.</summary>
    [Fact]
    public async Task An_upgrade_numbers_the_stored_workspaces_in_yard_order()
    {
        await MigrateToAsync("20261001105022_SessionWindowFolders");
        const string first = "11111111-1111-1111-1111-111111111111", second = "22222222-2222-2222-2222-222222222222";
        await ExecuteAsync($"""
            INSERT INTO "Tracks" ("Id", "Name", "SortOrder") VALUES ('{second}', 'Later', 2), ('{first}', 'Earlier', 1);
            INSERT INTO "Workspaces" ("Id", "Name", "RootPath", "TrackId", "AccentColor", "HostMode", "AutoStart", "CreatedAt") VALUES
              ('A0000000-0000-0000-0000-000000000001', 'zeta', 'c:\z', '{first}', '#000000', 'Snap', 0, 0),
              ('A0000000-0000-0000-0000-000000000002', 'Alpha', 'c:\a', '{second}', '#000000', 'Snap', 0, 0),
              ('A0000000-0000-0000-0000-000000000003', 'beta', 'c:\b', '{first}', '#000000', 'Snap', 0, 0);
            """);

        await InitializeWithinTenSecondsAsync();

        (await _db.Get<IWorkspaceStore>().GetAllAsync(TestContext.Current.CancellationToken)).ToDictionary(w => w.Name, w => w.Number)
            .ShouldBe(new Dictionary<string, int> { ["beta"] = 1, ["zeta"] = 2, ["Alpha"] = 3 }, ignoreOrder: true);
    }

    [Fact]
    public async Task An_upgrade_marks_the_cursors_stored_before_titles_were_as_having_found_a_prompt_title()
    {
        await MigrateToAsync("20260924142231_RemoveSeededPricing");
        await ExecuteAsync("""INSERT INTO "TranscriptCursors" ("Path", "ByteOffset", "LastWriteUtc", "SessionId") VALUES ('c:\t\s1.jsonl', 10, 0, 's1');""");

        await InitializeWithinTenSecondsAsync();

        var cursor = (await _db.Get<IUsageStore>().GetCursorsAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        cursor.Title.ShouldBeNull();
        // The indexer of that time took the first prompt as the title without storing it: a later prompt must not rename
        // the chat, while a generated or /rename title still does.
        cursor.TitleSource.ShouldBe(TitleSource.Prompt);
    }

    [Fact]
    public async Task Every_connection_writes_with_synchronous_NORMAL_not_only_the_one_that_ran_the_initializer()
    {
        await _db.InitializeAsync();
        await using var first = await CreateContextAsync();
        await using var second = await CreateContextAsync();
        await first.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await second.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        (await SynchronousAsync(first)).ShouldBe(1L);
        (await SynchronousAsync(second)).ShouldBe(1L, "a fresh pooled connection stayed FULL: an extra WAL fsync per commit");
    }

    [Fact]
    public async Task A_connection_keeps_full_sync_until_the_initializer_has_the_database_in_write_ahead_logging()
    {
        // No initializer: a fresh file is in rollback-journal mode, where NORMAL can corrupt the database on a power loss.
        await using var db = await CreateContextAsync();
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        (await SynchronousAsync(db)).ShouldBe(2L, "FULL until write-ahead logging is on");
    }

    [Fact]
    public async Task The_pragma_runs_once_per_physical_connection_not_on_every_open()
    {
        await _db.InitializeAsync();
        var pragmas = _db.Get<SqlitePragmaInterceptor>();
        await using var db = await CreateContextAsync();
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        (await SynchronousAsync(db)).ShouldBe(1L);
        await db.Database.CloseConnectionAsync();
        var runs = pragmas.PragmaRuns;

        for (var i = 0; i < 3; i++)
        {
            // The pool hands the same handle back, and it keeps its pragmas.
            await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            (await SynchronousAsync(db)).ShouldBe(1L);
            await db.Database.CloseConnectionAsync();
        }

        pragmas.PragmaRuns.ShouldBe(runs, "a handle that has the pragma does not get it again on every open");
    }

    [Fact]
    public async Task A_pragma_that_fails_leaves_the_connection_closed_so_the_next_open_sets_it()
    {
        await _db.InitializeAsync();
        // Holds the pooled handle the initializer configured, so the opens below get fresh handles that still need the pragma.
        await using var holder = await CreateContextAsync();
        await holder.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var db = await CreateContextAsync();
        var connection = db.Database.GetDbConnection();
        using var cancelled = new CancellationTokenSource();
        // Cancelled the moment the connection is open: the pragma after it is what fails.
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Open)
            {
                cancelled.Cancel();
            }
        };

        await Should.ThrowAsync<OperationCanceledException>(() => db.Database.OpenConnectionAsync(cancelled.Token));

        connection.State.ShouldBe(System.Data.ConnectionState.Closed, "EF Core did not record the open, so it would neither close the connection nor configure it again");
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        (await SynchronousAsync(db)).ShouldBe(1L);
    }

    [Fact]
    public async Task A_database_with_only_the_first_migration_applied_is_upgraded_with_its_rows()
    {
        await MigrateToInitialCreateAsync();
        await ExecuteAsync("""INSERT INTO "Settings" ("Key", "ValueJson") VALUES ('budget', '5');""");

        await InitializeWithinTenSecondsAsync();

        await using var migrated = await CreateContextAsync();
        (await migrated.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await _db.Get<ISettingsStore>().GetAsync<long?>("budget", TestContext.Current.CancellationToken)).ShouldBe(5L);
    }

    /// <summary>PRAGMA synchronous: 0 OFF, 1 NORMAL, 2 FULL. A pragma cannot sit in a subquery, so not through EF's query pipeline.</summary>
    private static async Task<long> SynchronousAsync(CodeSwitchXDbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA synchronous;";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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

    [Fact]
    public async Task An_upgrade_leaves_a_stored_rules_one_hour_rate_unset_so_the_lookup_derives_it_from_the_input()
    {
        await MigrateToAsync("20260928191609_CursorTitles");
        await ExecuteAsync("""INSERT INTO "PricingRules" ("Model", "InputPerM", "OutputPerM", "CacheWritePerM", "CacheReadPerM", "ContextWindow") VALUES ('claude-sonnet-5', 3, 15, 3.75, 0.3, 200000);""");

        await InitializeWithinTenSecondsAsync();

        // A multiplier fixed into the row at the upgrade would never follow a later change of the shipped one (the reason
        // RemoveSeededPricing exists); the estimator derives the rate from the input at every lookup instead.
        (await ExecuteAsync("""SELECT "CacheWrite1hPerM" IS NULL FROM "PricingRules" WHERE "Model" = 'claude-sonnet-5';""")).ShouldBe(1L);
        (await _db.Get<ISettingsStore>().GetPricingAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().CacheWrite1hPerM.ShouldBeNull();
    }
}
