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
}
