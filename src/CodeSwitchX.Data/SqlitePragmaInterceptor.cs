using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSwitchX.Data;

/// <summary>
/// <c>PRAGMA synchronous</c> is per connection: run by the initializer alone it reached only the pooled connection that
/// ran it, and every other one stayed FULL, an extra WAL fsync per commit. NORMAL is safe in WAL mode: a crash can lose
/// the last transactions, never the database. The pragma is cheap, so a pooled connection gets it again on every open.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
