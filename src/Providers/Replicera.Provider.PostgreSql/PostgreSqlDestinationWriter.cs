using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql;

public sealed class PostgreSqlDestinationWriter(string connectionString) : IDestinationWriter
{
    public Task<IReplicationSession> BeginInitialSyncAsync(
        string jobName,
        TableDefinition table,
        CancellationToken cancellationToken) => BeginAsync(jobName, table, "Initial", cancellationToken);

    public Task<IReplicationSession> BeginIncrementalSyncAsync(
        string jobName,
        TableDefinition table,
        string currentCheckpoint,
        CancellationToken cancellationToken) => BeginAsync(jobName, table, "Incremental", cancellationToken);

    private async Task<IReplicationSession> BeginAsync(
        string jobName,
        TableDefinition table,
        string syncType,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        NpgsqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tableId = await EnsureManagedTableAsync(connection, jobName, table, cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await AcquireLockAsync(connection, transaction, jobName, table.LogicalName, cancellationToken).ConfigureAwait(false);
            var runId = Guid.NewGuid();
            await CreateRunMetadataAsync(connection, transaction, runId, tableId, jobName, syncType, cancellationToken).ConfigureAwait(false);
            var stagingName = PostgreSqlIdentifier.Normalize($"replicera_stage_{runId:N}");
            await ExecuteAsync(connection, transaction, PostgreSqlDmlBuilder.BuildCreateStaging(table, stagingName), cancellationToken).ConfigureAwait(false);
            if (syncType == "Initial")
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"DELETE FROM {PostgreSqlIdentifier.Qualified("public", table.DestinationName)};",
                    cancellationToken).ConfigureAwait(false);
            }

            return new Session(connection, transaction, runId, tableId, table, stagingName, syncType);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Guid> EnsureManagedTableAsync(
        NpgsqlConnection connection,
        string jobName,
        TableDefinition table,
        CancellationToken cancellationToken)
    {
        await using (var lookup = new NpgsqlCommand("""
            SELECT table_id
            FROM replicera.tables
            WHERE replication_job_id = @job_name AND dataverse_logical_name = @logical_name;
            """, connection))
        {
            lookup.Parameters.AddWithValue("job_name", jobName);
            lookup.Parameters.AddWithValue("logical_name", table.LogicalName);
            var existing = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (existing is Guid existingId)
            {
                return existingId;
            }
        }

        var tableId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO replicera.tables
                (table_id, replication_job_id, dataverse_logical_name, dataverse_entity_set_name,
                 destination_schema, destination_table_name, primary_key, change_tracking_enabled, status)
            VALUES
                (@table_id, @job_name, @logical_name, @entity_set_name,
                 'public', @destination_name, @primary_key, TRUE, 'Uninitialized')
            ON CONFLICT (replication_job_id, dataverse_logical_name)
            DO UPDATE SET replication_job_id = EXCLUDED.replication_job_id
            RETURNING table_id;
            """, connection);
        command.Parameters.AddWithValue("table_id", tableId);
        command.Parameters.AddWithValue("job_name", jobName);
        command.Parameters.AddWithValue("logical_name", table.LogicalName);
        command.Parameters.AddWithValue("entity_set_name", table.EntitySetName);
        command.Parameters.AddWithValue("destination_name", PostgreSqlIdentifier.Normalize(table.DestinationName));
        command.Parameters.AddWithValue("primary_key", table.PrimaryKey.LogicalName);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL did not return the managed table ID."));
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_xact_lock(hashtextextended(@resource, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("resource", $"replicera:{jobName}:{logicalName}");
        var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
        if (!acquired)
        {
            throw new InvalidOperationException($"A synchronization is already running for job '{jobName}', table '{logicalName}'.");
        }
    }

    private static async Task CreateRunMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        Guid tableId,
        string jobName,
        string syncType,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE replicera.tables SET status = 'Syncing' WHERE table_id = @table_id;
            INSERT INTO replicera.sync_runs
                (sync_run_id, replication_job_id, table_id, sync_type, started_utc, status)
            VALUES (@run_id, @job_name, @table_id, @sync_type, CURRENT_TIMESTAMP, 'Running');
            """, connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("table_id", tableId);
        command.Parameters.AddWithValue("job_name", jobName);
        command.Parameters.AddWithValue("sync_type", syncType);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // SQL is generated solely from normalized and quoted provider-owned identifiers.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Session(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        Guid tableId,
        TableDefinition table,
        string stagingName,
        string syncType) : IReplicationSession
    {
        private bool committed;

        public async Task<PageApplyResult> ApplyPageAsync(SourcePage page, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(committed, this);
            if (page.Records.Count == 0)
            {
                return new PageApplyResult(0, 0, 0);
            }

            await CopyPageAsync(page, cancellationToken).ConfigureAwait(false);
            var result = await CountOperationsAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, PostgreSqlDmlBuilder.BuildApplyStaging(table, stagingName), cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, PostgreSqlDmlBuilder.BuildTruncateStaging(stagingName), cancellationToken).ConfigureAwait(false);
            return result;
        }

        public async Task CommitAsync(string newCheckpoint, SyncMetrics metrics, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(committed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(newCheckpoint);
            await ExecuteAsync(connection, transaction, PostgreSqlDmlBuilder.BuildDropStaging(stagingName), cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("""
                UPDATE replicera.tables
                SET change_checkpoint = @checkpoint,
                    last_successful_sync_utc = CURRENT_TIMESTAMP,
                    last_incremental_sync_utc = CASE WHEN @sync_type = 'Incremental' THEN CURRENT_TIMESTAMP ELSE last_incremental_sync_utc END,
                    last_full_sync_utc = CASE WHEN @sync_type = 'Initial' THEN CURRENT_TIMESTAMP ELSE last_full_sync_utc END,
                    status = 'Healthy'
                WHERE table_id = @table_id;

                UPDATE replicera.sync_runs
                SET completed_utc = CURRENT_TIMESTAMP, records_received = @received,
                    records_inserted = @inserted, records_updated = @updated,
                    records_deleted = @deleted, pages_processed = @pages, status = 'Succeeded'
                WHERE sync_run_id = @run_id;
                """, connection, transaction);
            command.Parameters.AddWithValue("checkpoint", newCheckpoint);
            command.Parameters.AddWithValue("table_id", tableId);
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("sync_type", syncType);
            command.Parameters.AddWithValue("received", metrics.RecordsReceived);
            command.Parameters.AddWithValue("inserted", metrics.RecordsInserted);
            command.Parameters.AddWithValue("updated", metrics.RecordsUpdated);
            command.Parameters.AddWithValue("deleted", metrics.RecordsDeleted);
            command.Parameters.AddWithValue("pages", metrics.PagesProcessed);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        private async Task CopyPageAsync(SourcePage page, CancellationToken cancellationToken)
        {
            var layout = PostgreSqlTableLayout.GetColumns(table);
            var columnList = string.Join(", ", layout.Select(column => PostgreSqlIdentifier.Quote(column.Name))
                .Append(PostgreSqlIdentifier.Quote(PostgreSqlDmlBuilder.OperationColumn)));
            var copy = $"COPY {PostgreSqlIdentifier.Quote(stagingName)} ({columnList}) FROM STDIN (FORMAT BINARY)";
#pragma warning disable CA2100 // COPY targets normalized provider-owned identifiers.
            await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2100
            foreach (var record in page.Records)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                foreach (var column in layout)
                {
                    var value = GetValue(column, record);
                    if (value is null)
                    {
                        await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var dbType = column.IsLookupTarget
                            ? NpgsqlDbType.Varchar
                            : PostgreSqlTypeMapper.Map(column.Source).DbType;
                        await importer.WriteAsync(value, dbType, cancellationToken).ConfigureAwait(false);
                    }
                }

                await importer.WriteAsync(
                    record.Kind == ChangeKind.Delete ? "D" : "U",
                    NpgsqlDbType.Char,
                    cancellationToken).ConfigureAwait(false);
            }

            _ = await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<PageApplyResult> CountOperationsAsync(CancellationToken cancellationToken)
        {
            var layout = PostgreSqlTableLayout.GetColumns(table);
            var key = PostgreSqlIdentifier.Quote(layout.Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget).Name);
            var target = PostgreSqlIdentifier.Qualified("public", table.DestinationName);
            var staging = PostgreSqlIdentifier.Quote(stagingName);
            var operation = PostgreSqlIdentifier.Quote(PostgreSqlDmlBuilder.OperationColumn);
#pragma warning disable CA2100 // SQL is generated solely from normalized and quoted provider-owned identifiers.
            await using var command = new NpgsqlCommand($"""
                SELECT
                    COUNT(*) FILTER (WHERE source.{operation} = 'U' AND target.{key} IS NULL),
                    COUNT(*) FILTER (WHERE source.{operation} = 'U' AND target.{key} IS NOT NULL),
                    COUNT(*) FILTER (WHERE source.{operation} = 'D' AND target.{key} IS NOT NULL)
                FROM {staging} AS source
                LEFT JOIN {target} AS target ON target.{key} = source.{key};
                """, connection, transaction);
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new PageApplyResult(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        private static object? GetValue(PostgreSqlPhysicalColumn column, SourceRecord record)
        {
            if (column.Source.IsPrimaryKey)
            {
                return record.Id;
            }

            if (record.Kind == ChangeKind.Delete
                || !record.Values.TryGetValue(column.Source.LogicalName, out var value)
                || value is null)
            {
                return null;
            }

            if (column.IsLookupTarget)
            {
                return value is LookupValue lookup ? lookup.TargetLogicalName : null;
            }

            return value switch
            {
                LookupValue lookup => lookup.Id,
                ChoiceSetValue choices => JsonSerializer.Serialize(choices.Values),
                DateTime dateTime when column.Source.SourceType == SourceType.DateTime
                                       && column.Source.DateTimeBehavior == DateTimeBehavior.DateOnly => DateOnly.FromDateTime(dateTime),
                DateTime dateTime when column.Source.SourceType == SourceType.DateTime
                                       && column.Source.DateTimeBehavior == DateTimeBehavior.UserLocal => ToUtc(dateTime),
                DateTime dateTime when column.Source.SourceType == SourceType.DateTime =>
                    DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
                DateOnly date => date,
                _ => value
            };
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
