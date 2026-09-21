using Npgsql;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql;

public sealed class PostgreSqlReplicationStateStore(string connectionString) : IReplicationStateStore
{
    public async Task<TableReplicationState?> GetTableStateAsync(
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var metadataCommand = new NpgsqlCommand("SELECT to_regclass('replicera.tables') IS NOT NULL;", connection))
        {
            var hasMetadata = (bool)(await metadataCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
            if (!hasMetadata)
            {
                return null;
            }
        }

        await using var command = new NpgsqlCommand("""
            SELECT t.status, t.change_checkpoint, t.last_successful_sync_utc,
                   latest.sync_type, latest.started_utc, latest.completed_utc,
                   latest.records_inserted, latest.records_updated, latest.records_deleted,
                   latest.error_code, latest.error_message
            FROM replicera.tables AS t
            LEFT JOIN LATERAL
            (
                SELECT r.sync_type, r.started_utc, r.completed_utc,
                       r.records_inserted, r.records_updated, r.records_deleted,
                       r.error_code, r.error_message
                FROM replicera.sync_runs AS r
                WHERE r.table_id = t.table_id
                ORDER BY r.started_utc DESC, r.sync_run_id DESC
                LIMIT 1
            ) AS latest ON TRUE
            WHERE t.replication_job_id = @job_name AND t.dataverse_logical_name = @logical_name;
            """, connection);
        command.Parameters.AddWithValue("job_name", jobName);
        command.Parameters.AddWithValue("logical_name", logicalName);
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
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            WITH target AS
            (
                SELECT table_id
                FROM replicera.tables
                WHERE replication_job_id = @job_name AND dataverse_logical_name = @logical_name
            ), updated_table AS
            (
                UPDATE replicera.tables
                SET status = @status
                WHERE table_id IN (SELECT table_id FROM target)
            ), latest AS
            (
                SELECT sync_run_id
                FROM replicera.sync_runs
                WHERE table_id IN (SELECT table_id FROM target) AND status = 'Running'
                ORDER BY started_utc DESC
                LIMIT 1
            ), updated_run AS
            (
                UPDATE replicera.sync_runs
                SET completed_utc = CURRENT_TIMESTAMP, status = 'Failed',
                    error_code = @error_code, error_message = @message
                WHERE sync_run_id IN (SELECT sync_run_id FROM latest)
                RETURNING sync_run_id
            )
            INSERT INTO replicera.sync_runs
                (sync_run_id, replication_job_id, table_id, sync_type, started_utc, completed_utc,
                 status, error_code, error_message)
            SELECT @run_id, @job_name, table_id, 'Unknown', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP,
                   'Failed', @error_code, @message
            FROM target
            WHERE NOT EXISTS (SELECT 1 FROM updated_run);
            """, connection);
        command.Parameters.AddWithValue("job_name", jobName);
        command.Parameters.AddWithValue("logical_name", logicalName);
        command.Parameters.AddWithValue("status", state.ToString());
        command.Parameters.AddWithValue("error_code", errorCode);
        command.Parameters.AddWithValue("message", sanitizedMessage[..Math.Min(sanitizedMessage.Length, 2048)]);
        command.Parameters.AddWithValue("run_id", Guid.NewGuid());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
