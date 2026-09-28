using CodeSwitchX.Core.Persistence;

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
