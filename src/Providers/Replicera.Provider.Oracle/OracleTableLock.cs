using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace Replicera.Provider.Oracle;

internal sealed class OracleTableLock(OracleConnection connection, int lockId) : IAsyncDisposable
{
    public static async Task<IAsyncDisposable> AcquireAsync(
        string connectionString,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var resource = $"replicera:{jobName}:{logicalName}";
            var lockId = await GetLockIdAsync(connection, resource, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandText = "BEGIN :result := DBMS_LOCK.REQUEST(:lock_id, 6, 0, FALSE); END;";
            var result = command.Parameters.Add("result", OracleDbType.Int32);
            result.Direction = ParameterDirection.Output;
            command.Parameters.Add("lock_id", OracleDbType.Int32).Value = lockId;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt32(result.Value, System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException($"A synchronization is already running for job '{jobName}', table '{logicalName}'.");
            }

            return new OracleTableLock(connection, lockId);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> GetLockIdAsync(
        OracleConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = "SELECT DBMS_UTILITY.GET_HASH_VALUE(:resource, 0, 1073741823) FROM DUAL";
        command.Parameters.Add("resource", OracleDbType.NVarchar2).Value = resource;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.BindByName = true;
                command.CommandText = "BEGIN :result := DBMS_LOCK.RELEASE(:lock_id); END;";
                var result = command.Parameters.Add("result", OracleDbType.Int32);
                result.Direction = ParameterDirection.Output;
                command.Parameters.Add("lock_id", OracleDbType.Int32).Value = lockId;
                _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
