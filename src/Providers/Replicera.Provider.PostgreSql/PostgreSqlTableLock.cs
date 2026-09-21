using Npgsql;

namespace Replicera.Provider.PostgreSql;

internal sealed class PostgreSqlTableLock(NpgsqlConnection connection, string resource) : IAsyncDisposable
{
    public static async Task<IAsyncDisposable> AcquireAsync(
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
                throw new InvalidOperationException($"A synchronization is already running for job '{jobName}', table '{logicalName}'.");
            }

            return new PostgreSqlTableLock(connection, resource);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
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
