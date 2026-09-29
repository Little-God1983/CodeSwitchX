using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CodeSwitchX.Data.Tests;

public class SettingsStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private ISettingsStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<ISettingsStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private sealed record Hotkeys(string Toggle, int Count);

    /// <summary>The settings table and its value column under other names, as a later migration might map them.</summary>
    private sealed class RenamedSettings(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<Setting>().ToTable("Preferences").Property(s => s.ValueJson).HasColumnName("Json");
        }
    }

    [Fact]
    public async Task A_setting_is_saved_where_the_model_maps_it_not_where_the_SQL_was_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<CodeSwitchXDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_db.Root, "renamed.db")}")
            .ReplaceService<IModelCustomizer, RenamedSettings>()
            .Options;
        var factory = new PooledDbContextFactory<CodeSwitchXDbContext>(options);
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.EnsureCreatedAsync(ct);
        }

        var store = new SettingsStore(factory);
        await store.SetAsync("budget", 5L, ct);
        await store.SetAsync("budget", 6L, ct);

        (await store.GetAsync<long?>("budget", ct)).ShouldBe(6L);
    }

    [Fact]
    public async Task Settings_round_trip_as_json()
    {
        (await _store.GetAsync<Hotkeys>("hotkeys", TestContext.Current.CancellationToken)).ShouldBeNull();

        await _store.SetAsync("hotkeys", new Hotkeys("Ctrl+Alt+Y", 3), TestContext.Current.CancellationToken);
        await _store.SetAsync("budget", 1_000_000L, TestContext.Current.CancellationToken);

        (await _store.GetAsync<Hotkeys>("hotkeys", TestContext.Current.CancellationToken)).ShouldBe(new Hotkeys("Ctrl+Alt+Y", 3));
        (await _store.GetAsync<long?>("budget", TestContext.Current.CancellationToken)).ShouldBe(1_000_000L);
    }

    [Fact]
    public async Task Pricing_defaults_fill_gaps_without_overwriting_user_edits()
    {
        await _store.UpsertPricingAsync([new PricingRule { Model = "claude-sonnet-5", InputPerM = 99 }], TestContext.Current.CancellationToken);

        await _store.EnsurePricingDefaultsAsync([
            new PricingRule { Model = "claude-sonnet-5", InputPerM = 3 },
            new PricingRule { Model = "claude-haiku-4-5", InputPerM = 1 },
        ], TestContext.Current.CancellationToken);

        var rules = (await _store.GetPricingAsync(TestContext.Current.CancellationToken)).ToDictionary(r => r.Model);
        rules["claude-sonnet-5"].InputPerM.ShouldBe(99);
        rules["claude-haiku-4-5"].InputPerM.ShouldBe(1);
    }

    [Fact]
    public async Task Two_first_saves_of_one_key_at_the_same_time_both_succeed()
    {
        // Each setting change used to be saved on a thread of its own, so the first two saves of a key could both find
        // no row and both insert, and the second failed on the primary key.
        for (var round = 0; round < 20; round++)
        {
            var key = "toggle-" + round;
            var ct = TestContext.Current.CancellationToken;

            await Task.WhenAll(Task.Run(() => _store.SetAsync(key, true, ct), ct), Task.Run(() => _store.SetAsync(key, false, ct), ct));

            (await _store.GetAsync<bool?>(key, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        }
    }
}
