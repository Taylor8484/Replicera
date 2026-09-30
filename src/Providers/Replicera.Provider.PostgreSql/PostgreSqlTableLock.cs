using Npgsql;
using Replicera.Core.Abstractions;
using Replicera.Core.Errors;

namespace Replicera.Provider.PostgreSql;

internal sealed class PostgreSqlTableLock(NpgsqlConnection connection, string resource, string jobName, string logicalName) : IDestinationTableLock
{
    public static async Task<IDestinationTableLock> AcquireAsync(
        string connectionString,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var resource = $"replicera:{jobName}:{logicalName}";
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(hashtextextended(@resource, 0));",
                connection);
            command.Parameters.AddWithValue("resource", resource);
            var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
            if (!acquired)
            {
                throw new SynchronizationAlreadyRunningException(jobName, logicalName);
            }

            return new PostgreSqlTableLock(connection, resource, jobName, logicalName);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The advisory lock must be held by the backend behind this connection. Behind a transaction-
    // pooling proxy the statement can reach a different backend, which fails this check safely.
    public async Task EnsureHeldAsync(CancellationToken cancellationToken)
    {
        bool held;
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND pid = pg_backend_pid()
                      AND ((classid::bigint << 32) | objid::bigint) = hashtextextended(@resource, 0));
                """,
                connection);
            command.Parameters.AddWithValue("resource", resource);
            held = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            throw new TableLockLostException(jobName, logicalName, exception);
        }

        if (!held)
        {
            throw new TableLockLostException(jobName, logicalName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@resource, 0));",
                    connection);
                command.Parameters.AddWithValue("resource", resource);
                _ = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
