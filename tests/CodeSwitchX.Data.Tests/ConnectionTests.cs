using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Tests;

/// <summary>How the app's connections share the database file (#273).</summary>
public sealed class ConnectionTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();

    public ValueTask InitializeAsync() => new(_db.InitializeAsync());

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task A_read_does_not_wait_for_a_write_in_progress()
    {
        // With a cache shared between connections, a read of a table being written waited for the commit, up to the
        // command timeout; with a cache of its own and write-ahead logging, it reads what was committed, at once.
        var ct = TestContext.Current.CancellationToken;
        await using var writer = await _db.Get<IDbContextFactory<CodeSwitchXDbContext>>().CreateDbContextAsync(ct);
        (await writer.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync(ct)).ShouldBe(["wal"], "what lets the read go on");
        await using var transaction = await writer.Database.BeginTransactionAsync(ct);
        writer.Tracks.Add(new Track { Name = "Not committed yet", SortOrder = 9 });
        await writer.SaveChangesAsync(ct);

        // Stopped after 5 s if it waits: the read itself ends, rather than going on past the test.
        using var waitedTooLong = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitedTooLong.CancelAfter(TimeSpan.FromSeconds(5));
        var tracks = await _db.Get<IWorkspaceStore>().GetTracksAsync(waitedTooLong.Token);

        tracks.ShouldNotContain(t => t.Name == "Not committed yet");
        await transaction.RollbackAsync(ct);
    }
}
