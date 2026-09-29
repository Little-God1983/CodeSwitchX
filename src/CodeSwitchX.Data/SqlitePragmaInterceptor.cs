using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSwitchX.Data;

/// <summary>
/// <c>PRAGMA synchronous</c> is per connection: run by the initializer alone it reached only the pooled connection that
/// ran it, and every other one stayed FULL, an extra WAL fsync per commit. NORMAL is safe in WAL mode only: there a
/// crash can lose the last transactions, never the database; in rollback-journal mode it can corrupt it. So the pragma
/// runs once <see cref="WriteAheadLog"/> says the initializer has the database in WAL mode, which puts the migrations
/// before it under FULL. Microsoft.Data.Sqlite pools the physical connections and hands them to any connection object,
/// with their pragmas as they were; a handle gets the pragma once, not on every open.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA synchronous=NORMAL;";
    private readonly ConditionalWeakTable<SafeHandle, object> _configured = [];
    private int _runs;

    /// <summary>Set by the initializer once <c>PRAGMA journal_mode=WAL</c> answered "wal"; until then every connection keeps FULL.</summary>
    public bool WriteAheadLog { get; set; }

    internal int PragmaRuns => _runs;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (Handle(connection) is not { } handle)
        {
            return;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = Pragmas;
            command.ExecuteNonQuery();
            Configured(handle);
        }
        catch
        {
            // EF Core has not recorded the open, so it would neither close the connection nor run this again on it.
            connection.Close();
            throw;
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Handle(connection) is not { } handle)
        {
            return;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Pragmas;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Configured(handle);
        }
        catch
        {
            await connection.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The physical connection behind this open, when it still needs the pragma.</summary>
    private SafeHandle? Handle(DbConnection connection)
    {
        if (!WriteAheadLog || connection is not SqliteConnection { Handle: { } handle })
        {
            return null;
        }

        return _configured.TryGetValue(handle, out _) ? null : handle;
    }

    private void Configured(SafeHandle handle)
    {
        _configured.AddOrUpdate(handle, handle);
        Interlocked.Increment(ref _runs);
    }
}
