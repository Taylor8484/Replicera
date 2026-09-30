using System.Data;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Replicera.Core.Abstractions;
using Replicera.Core.Errors;

namespace Replicera.Provider.Oracle;

internal sealed class OracleTableLock(OracleConnection connection, int lockId, string jobName, string logicalName) : IDestinationTableLock
{
    // DBMS_LOCK.REQUEST results: 0 granted, 1 timeout (held by another session), 4 already held by
    // this session. Other values report parameter or handle errors.
    private const int Granted = 0;
    private const int HeldByAnotherSession = 1;
    private const int AlreadyHeldBySession = 4;

    public static async Task<IDestinationTableLock> AcquireAsync(
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
            var resultCode = await RequestAsync(connection, lockId, cancellationToken).ConfigureAwait(false);
            if (resultCode == HeldByAnotherSession)
            {
                throw new SynchronizationAlreadyRunningException(jobName, logicalName);
            }

            if (resultCode is not (Granted or AlreadyHeldBySession))
            {
                throw new RepliceraException(
                    ErrorCategory.Synchronization,
                    $"Oracle DBMS_LOCK.REQUEST returned {resultCode} for job '{jobName}', table '{logicalName}'.");
            }

            return new OracleTableLock(connection, lockId, jobName, logicalName);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Requesting the lock again from the owning session reports that it is already held without
    // needing access to V$LOCK. A fresh grant means the lock had been released, so it is dropped
    // again and treated as lost.
    public async Task EnsureHeldAsync(CancellationToken cancellationToken)
    {
        int resultCode;
        try
        {
            resultCode = await RequestAsync(connection, lockId, cancellationToken).ConfigureAwait(false);
            if (resultCode == Granted)
            {
                await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OracleException or InvalidOperationException)
        {
            throw new TableLockLostException(jobName, logicalName, exception);
        }

        if (resultCode != AlreadyHeldBySession)
        {
            throw new TableLockLostException(jobName, logicalName);
        }
    }

    private static async Task<int> RequestAsync(OracleConnection connection, int lockId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = "BEGIN :result := DBMS_LOCK.REQUEST(:lock_id, 6, 0, FALSE); END;";
        var result = command.Parameters.Add("result", OracleDbType.Int32);
        result.Direction = ParameterDirection.Output;
        command.Parameters.Add("lock_id", OracleDbType.Int32).Value = lockId;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return result.Value is OracleDecimal oracleResult
            ? oracleResult.ToInt32()
            : Convert.ToInt32(result.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> GetLockIdAsync(
        OracleConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = "SELECT DBMS_UTILITY.GET_HASH_VALUE(:p_lock_name, 0, 1073741823) FROM DUAL";
        command.Parameters.Add("p_lock_name", OracleDbType.NVarchar2).Value = resource;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = "BEGIN :result := DBMS_LOCK.RELEASE(:lock_id); END;";
        var result = command.Parameters.Add("result", OracleDbType.Int32);
        result.Direction = ParameterDirection.Output;
        command.Parameters.Add("lock_id", OracleDbType.Int32).Value = lockId;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
