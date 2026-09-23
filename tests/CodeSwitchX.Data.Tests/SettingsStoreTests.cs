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
        (await _store.GetAsync<Hotkeys>("hotkeys")).ShouldBeNull();

        await _store.SetAsync("hotkeys", new Hotkeys("Ctrl+Alt+Y", 3));
        await _store.SetAsync("budget", 1_000_000L);

        (await _store.GetAsync<Hotkeys>("hotkeys")).ShouldBe(new Hotkeys("Ctrl+Alt+Y", 3));
        (await _store.GetAsync<long?>("budget")).ShouldBe(1_000_000L);
    }

    [Fact]
    public async Task Pricing_defaults_fill_gaps_without_overwriting_user_edits()
    {
        await _store.UpsertPricingAsync([new PricingRule { Model = "claude-sonnet-5", InputPerM = 99 }]);

        await _store.EnsurePricingDefaultsAsync(
        [
            new PricingRule { Model = "claude-sonnet-5", InputPerM = 3 },
            new PricingRule { Model = "claude-haiku-4-5", InputPerM = 1 },
        ]);

        var rules = (await _store.GetPricingAsync()).ToDictionary(r => r.Model);
        rules["claude-sonnet-5"].InputPerM.ShouldBe(99);
        rules["claude-haiku-4-5"].InputPerM.ShouldBe(1);
    }
}
