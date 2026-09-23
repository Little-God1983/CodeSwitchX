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
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            _logger.LogInformation("Applying {Count} database migrations", pending.Count);
        }

        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        if (!await db.Tracks.AnyAsync(ct))
        {
            db.Tracks.Add(new Track { Name = DefaultTrackName, SortOrder = 0 });
            await db.SaveChangesAsync(ct);
        }
    }
}
