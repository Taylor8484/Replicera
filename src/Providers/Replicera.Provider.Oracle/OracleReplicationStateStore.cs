using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;

namespace Replicera.Provider.Oracle;

public sealed class OracleReplicationStateStore(string connectionString) : IReplicationStateStore
{
    public async Task<TableReplicationState?> GetTableStateAsync(
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await MetadataExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            SELECT target.STATUS, target.CHANGE_CHECKPOINT,
                   SYS_EXTRACT_UTC(target.LAST_SUCCESSFUL_SYNC_UTC),
                   latest.SYNC_TYPE, SYS_EXTRACT_UTC(latest.STARTED_UTC), SYS_EXTRACT_UTC(latest.COMPLETED_UTC),
                   latest.RECORDS_INSERTED, latest.RECORDS_UPDATED, latest.RECORDS_DELETED,
                   latest.ERROR_CODE, latest.ERROR_MESSAGE
            FROM REPLICERA_TABLES target
            OUTER APPLY
            (
                SELECT runs.SYNC_TYPE, runs.STARTED_UTC, runs.COMPLETED_UTC,
                       runs.RECORDS_INSERTED, runs.RECORDS_UPDATED, runs.RECORDS_DELETED,
                       runs.ERROR_CODE, runs.ERROR_MESSAGE
                FROM REPLICERA_SYNC_RUNS runs
                WHERE runs.TABLE_ID = target.TABLE_ID
                ORDER BY runs.STARTED_UTC DESC, runs.SYNC_RUN_ID DESC
                FETCH FIRST 1 ROW ONLY
            ) latest
            WHERE target.REPLICATION_JOB_ID = :job_name AND target.DATAVERSE_LOGICAL_NAME = :logical_name
            """;
        command.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
        command.Parameters.Add("logical_name", OracleDbType.NVarchar2).Value = logicalName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var state = Enum.TryParse<TableState>(reader.GetString(0), true, out var parsed)
            ? parsed
            : TableState.Failed;
        return new TableReplicationState(
            jobName,
            logicalName,
            state,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            ReadUtc(reader, 2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ReadUtc(reader, 4),
            ReadUtc(reader, 5),
            reader.IsDBNull(6) ? null : Convert.ToInt64(reader.GetDecimal(6), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetDecimal(7), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(8) ? null : Convert.ToInt64(reader.GetDecimal(8), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    public async Task MarkFailureAsync(
        string jobName,
        string logicalName,
        TableState state,
        string errorCode,
        string sanitizedMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var tableId = await FindTableIdAsync(connection, transaction, jobName, logicalName, cancellationToken).ConfigureAwait(false);
        if (tableId is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (var updateTable = connection.CreateCommand())
        {
            updateTable.Transaction = transaction;
            updateTable.BindByName = true;
            updateTable.CommandText = "UPDATE REPLICERA_TABLES SET STATUS = :status WHERE TABLE_ID = :table_id";
            updateTable.Parameters.Add("status", OracleDbType.NVarchar2).Value = state.ToString();
            updateTable.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId.Value);
            _ = await updateTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var runId = await FindRunningRunIdAsync(connection, transaction, tableId.Value, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.BindByName = true;
        if (runId is null)
        {
            command.CommandText = """
                INSERT INTO REPLICERA_SYNC_RUNS
                    (SYNC_RUN_ID, REPLICATION_JOB_ID, TABLE_ID, SYNC_TYPE, STARTED_UTC, COMPLETED_UTC,
                     STATUS, ERROR_CODE, ERROR_MESSAGE)
                VALUES
                    (:run_id, :job_name, :table_id, 'Unknown', SYSTIMESTAMP, SYSTIMESTAMP,
                     'Failed', :error_code, :message)
                """;
            command.Parameters.Add("run_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(Guid.NewGuid());
            command.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
            command.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId.Value);
        }
        else
        {
            command.CommandText = """
                UPDATE REPLICERA_SYNC_RUNS
                SET COMPLETED_UTC = SYSTIMESTAMP, STATUS = 'Failed',
                    ERROR_CODE = :error_code, ERROR_MESSAGE = :message
                WHERE SYNC_RUN_ID = :run_id
                """;
            command.Parameters.Add("run_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(runId.Value);
        }

        command.Parameters.Add("error_code", OracleDbType.NVarchar2).Value = errorCode;
        command.Parameters.Add("message", OracleDbType.NVarchar2).Value = sanitizedMessage[..Math.Min(sanitizedMessage.Length, 2000)];
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> MetadataExistsAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = 'REPLICERA_TABLES'";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<Guid?> FindTableIdAsync(
        OracleConnection connection,
        OracleTransaction transaction,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.BindByName = true;
        command.CommandText = "SELECT TABLE_ID FROM REPLICERA_TABLES WHERE REPLICATION_JOB_ID = :job_name AND DATAVERSE_LOGICAL_NAME = :logical_name";
        command.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
        command.Parameters.Add("logical_name", OracleDbType.NVarchar2).Value = logicalName;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is byte[] bytes ? OracleValueConverter.ToGuid(bytes) : null;
    }

    private static async Task<Guid?> FindRunningRunIdAsync(
        OracleConnection connection,
        OracleTransaction transaction,
        Guid tableId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.BindByName = true;
        command.CommandText = """
            SELECT SYNC_RUN_ID
            FROM REPLICERA_SYNC_RUNS
            WHERE TABLE_ID = :table_id AND STATUS = 'Running'
            ORDER BY STARTED_UTC DESC
            FETCH FIRST 1 ROW ONLY
            """;
        command.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is byte[] bytes ? OracleValueConverter.ToGuid(bytes) : null;
    }

    private static DateTimeOffset? ReadUtc(OracleDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    }
}
