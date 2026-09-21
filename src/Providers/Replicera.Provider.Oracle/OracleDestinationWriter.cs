using System.Data;
using System.Text.Json;
using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;

namespace Replicera.Provider.Oracle;

public sealed class OracleDestinationWriter(string connectionString) : IDestinationWriter
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
        var connection = new OracleConnection(connectionString);
        OracleTransaction? transaction = null;
        string? stagingName = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tableId = await FindManagedTableAsync(connection, jobName, table.LogicalName, cancellationToken).ConfigureAwait(false);
            var runId = Guid.NewGuid();
            stagingName = OracleIdentifier.Normalize($"REPLICERA_STAGE_{runId:N}");
            await ExecuteAsync(
                connection,
                null,
                OracleDmlBuilder.BuildCreateStaging(table, stagingName),
                cancellationToken).ConfigureAwait(false);
            transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            await AcquireLockAsync(connection, transaction, jobName, table.LogicalName, cancellationToken).ConfigureAwait(false);
            await CreateRunMetadataAsync(connection, transaction, runId, tableId, jobName, syncType, cancellationToken).ConfigureAwait(false);
            if (syncType == "Initial")
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"DELETE FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))}",
                    cancellationToken).ConfigureAwait(false);
            }

            return new Session(connection, transaction, runId, tableId, table, stagingName, syncType);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            if (stagingName is not null && connection.State == ConnectionState.Open)
            {
                await TryDropStagingAsync(connection, stagingName).ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Guid> FindManagedTableAsync(
        OracleConnection connection,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = "SELECT TABLE_ID FROM REPLICERA_TABLES WHERE REPLICATION_JOB_ID = :job_name AND DATAVERSE_LOGICAL_NAME = :logical_name";
        command.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
        command.Parameters.Add("logical_name", OracleDbType.NVarchar2).Value = logicalName;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is byte[] bytes
            ? OracleValueConverter.ToGuid(bytes)
            : throw new InvalidOperationException($"Table '{logicalName}' is not managed by Replicera.");
    }

    private static async Task AcquireLockAsync(
        OracleConnection connection,
        OracleTransaction transaction,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.BindByName = true;
            command.CommandText = """
                SELECT TABLE_ID
                FROM REPLICERA_TABLES
                WHERE REPLICATION_JOB_ID = :job_name AND DATAVERSE_LOGICAL_NAME = :logical_name
                FOR UPDATE NOWAIT
                """;
            command.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
            command.Parameters.Add("logical_name", OracleDbType.NVarchar2).Value = logicalName;
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException exception) when (exception.Number == 54)
        {
            throw new InvalidOperationException(
                $"A synchronization is already running for job '{jobName}', table '{logicalName}'.",
                exception);
        }
    }

    private static async Task CreateRunMetadataAsync(
        OracleConnection connection,
        OracleTransaction transaction,
        Guid runId,
        Guid tableId,
        string jobName,
        string syncType,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.BindByName = true;
        update.CommandText = "UPDATE REPLICERA_TABLES SET STATUS = 'Syncing' WHERE TABLE_ID = :table_id";
        update.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
        _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.BindByName = true;
        insert.CommandText = """
            INSERT INTO REPLICERA_SYNC_RUNS
                (SYNC_RUN_ID, REPLICATION_JOB_ID, TABLE_ID, SYNC_TYPE, STARTED_UTC, STATUS)
            VALUES (:run_id, :job_name, :table_id, :sync_type, SYSTIMESTAMP, 'Running')
            """;
        insert.Parameters.Add("run_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(runId);
        insert.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
        insert.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
        insert.Parameters.Add("sync_type", OracleDbType.NVarchar2).Value = syncType;
        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        OracleConnection connection,
        OracleTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // SQL is generated solely from normalized and quoted provider-owned identifiers.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryDropStagingAsync(OracleConnection connection, string stagingName)
    {
        try
        {
            await ExecuteAsync(
                connection,
                null,
                OracleDmlBuilder.BuildDropStaging(stagingName),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OracleException)
        {
            // Cleanup is best effort. The committed target rows and checkpoint remain authoritative.
        }
    }

    private sealed class Session(
        OracleConnection connection,
        OracleTransaction transaction,
        Guid runId,
        Guid tableId,
        TableDefinition table,
        string stagingName,
        string syncType) : IReplicationSession
    {
        private bool completed;

        public async Task<PageApplyResult> ApplyPageAsync(SourcePage page, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(completed, this);
            if (page.Records.Count == 0)
            {
                return new PageApplyResult(0, 0, 0);
            }

            await InsertStagingAsync(page, cancellationToken).ConfigureAwait(false);
            var result = await CountOperationsAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, OracleDmlBuilder.BuildApplyStaging(table, stagingName), cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, OracleDmlBuilder.BuildDeleteStagingChanges(table, stagingName), cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, OracleDmlBuilder.BuildClearStaging(stagingName), cancellationToken).ConfigureAwait(false);
            return result;
        }

        public async Task CommitAsync(string newCheckpoint, SyncMetrics metrics, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(completed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(newCheckpoint);
            await using (var updateTable = connection.CreateCommand())
            {
                updateTable.Transaction = transaction;
                updateTable.BindByName = true;
                updateTable.CommandText = """
                    UPDATE REPLICERA_TABLES
                    SET CHANGE_CHECKPOINT = :checkpoint,
                        LAST_SUCCESSFUL_SYNC_UTC = SYSTIMESTAMP,
                        LAST_INCREMENTAL_SYNC_UTC = CASE WHEN :sync_type = 'Incremental' THEN SYSTIMESTAMP ELSE LAST_INCREMENTAL_SYNC_UTC END,
                        LAST_FULL_SYNC_UTC = CASE WHEN :sync_type = 'Initial' THEN SYSTIMESTAMP ELSE LAST_FULL_SYNC_UTC END,
                        STATUS = 'Healthy'
                    WHERE TABLE_ID = :table_id
                    """;
                updateTable.Parameters.Add("checkpoint", OracleDbType.NClob).Value = newCheckpoint;
                updateTable.Parameters.Add("sync_type", OracleDbType.NVarchar2).Value = syncType;
                updateTable.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
                _ = await updateTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var updateRun = connection.CreateCommand())
            {
                updateRun.Transaction = transaction;
                updateRun.BindByName = true;
                updateRun.CommandText = """
                    UPDATE REPLICERA_SYNC_RUNS
                    SET COMPLETED_UTC = SYSTIMESTAMP, RECORDS_RECEIVED = :received,
                        RECORDS_INSERTED = :inserted, RECORDS_UPDATED = :updated,
                        RECORDS_DELETED = :deleted, PAGES_PROCESSED = :pages, STATUS = 'Succeeded'
                    WHERE SYNC_RUN_ID = :run_id
                    """;
                updateRun.Parameters.Add("received", OracleDbType.Int64).Value = metrics.RecordsReceived;
                updateRun.Parameters.Add("inserted", OracleDbType.Int64).Value = metrics.RecordsInserted;
                updateRun.Parameters.Add("updated", OracleDbType.Int64).Value = metrics.RecordsUpdated;
                updateRun.Parameters.Add("deleted", OracleDbType.Int64).Value = metrics.RecordsDeleted;
                updateRun.Parameters.Add("pages", OracleDbType.Int64).Value = metrics.PagesProcessed;
                updateRun.Parameters.Add("run_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(runId);
                _ = await updateRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
            await TryDropStagingAsync(connection, stagingName).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (!completed)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                completed = true;
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await TryDropStagingAsync(connection, stagingName).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        private async Task InsertStagingAsync(SourcePage page, CancellationToken cancellationToken)
        {
            var layout = OracleTableLayout.GetColumns(table);
            var columnNames = layout.Select(column => OracleIdentifier.Quote(column.Name))
                .Append(OracleIdentifier.Quote(OracleDmlBuilder.OperationColumn))
                .ToArray();
            var parameterNames = Enumerable.Range(0, columnNames.Length).Select(index => $":p{index}").ToArray();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.BindByName = true;
            command.ArrayBindCount = page.Records.Count;
#pragma warning disable CA2100 // Identifiers are normalized and quoted by the provider.
            command.CommandText = $"INSERT INTO {OracleIdentifier.Quote(stagingName)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", parameterNames)})";
#pragma warning restore CA2100
            for (var index = 0; index < layout.Count; index++)
            {
                var column = layout[index];
                var type = column.IsLookupTarget
                    ? new OracleType("NVARCHAR2(128)", OracleDbType.NVarchar2)
                    : OracleTypeMapper.Map(column.Source);
                var values = page.Records.Select(record => GetValue(column, record)).ToArray();
                AddArrayParameter(command, $"p{index}", type.DbType, values);
            }

            AddArrayParameter(
                command,
                $"p{layout.Count}",
                OracleDbType.Char,
                page.Records.Select(record => (object)(record.Kind == ChangeKind.Delete ? "D" : "U")).ToArray());
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<PageApplyResult> CountOperationsAsync(CancellationToken cancellationToken)
        {
            var key = OracleIdentifier.Quote(OracleTableLayout.GetColumns(table)
                .Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget).Name);
            var target = OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName));
            var staging = OracleIdentifier.Quote(stagingName);
            var operation = OracleIdentifier.Quote(OracleDmlBuilder.OperationColumn);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
#pragma warning disable CA2100 // SQL is generated solely from normalized and quoted provider-owned identifiers.
            command.CommandText = $"""
                SELECT
                    SUM(CASE WHEN source.{operation} = 'U' AND target.{key} IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN source.{operation} = 'U' AND target.{key} IS NOT NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN source.{operation} = 'D' AND target.{key} IS NOT NULL THEN 1 ELSE 0 END)
                FROM {staging} source
                LEFT JOIN {target} target ON target.{key} = source.{key}
                """;
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new PageApplyResult(
                reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetDecimal(0), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetDecimal(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetDecimal(2), System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void AddArrayParameter(
            OracleCommand command,
            string name,
            OracleDbType type,
            object[] values)
        {
            var parameter = command.Parameters.Add(name, type);
            parameter.Value = values;
            if (type is OracleDbType.NVarchar2 or OracleDbType.Varchar2 or OracleDbType.Char)
            {
                parameter.Size = Math.Max(1, values.Where(value => value is not DBNull).Select(value => value.ToString()?.Length ?? 0).DefaultIfEmpty(1).Max());
                parameter.ArrayBindSize = values.Select(value => value is DBNull ? 0 : value.ToString()?.Length ?? 0).ToArray();
            }
            else if (type == OracleDbType.Raw)
            {
                parameter.Size = 16;
            }
        }

        private static object GetValue(OraclePhysicalColumn column, SourceRecord record)
        {
            if (column.Source.IsPrimaryKey)
            {
                return OracleValueConverter.ToBytes(record.Id);
            }

            if (record.Kind == ChangeKind.Delete
                || !record.Values.TryGetValue(column.Source.LogicalName, out var value)
                || value is null)
            {
                return DBNull.Value;
            }

            if (column.IsLookupTarget)
            {
                return value is LookupValue lookup ? lookup.TargetLogicalName : DBNull.Value;
            }

            return value switch
            {
                Guid guid => OracleValueConverter.ToBytes(guid),
                LookupValue lookup => OracleValueConverter.ToBytes(lookup.Id),
                ChoiceSetValue choices => JsonSerializer.Serialize(choices.Values),
                bool boolean => boolean ? (short)1 : (short)0,
                DateTime dateTime when column.Source.DateTimeBehavior == DateTimeBehavior.DateOnly => dateTime.Date,
                DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                DateTime dateTime when column.Source.DateTimeBehavior == DateTimeBehavior.UserLocal => new DateTimeOffset(ToUtc(dateTime)),
                DateTime dateTime when column.Source.SourceType == SourceType.DateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
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
