using System.Data;
using Microsoft.Data.SqlClient;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer;

public sealed class SqlServerDestinationWriter(string connectionString) : IDestinationWriter
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
        var connection = new SqlConnection(connectionString);
        SqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tableId = await EnsureManagedTableAsync(
                connection,
                jobName,
                table,
                cancellationToken).ConfigureAwait(false);
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
            await AcquireLockAsync(connection, transaction, jobName, table.LogicalName, cancellationToken).ConfigureAwait(false);

            var runId = Guid.NewGuid();
            await CreateRunMetadataAsync(
                connection,
                transaction,
                runId,
                tableId,
                jobName,
                syncType,
                cancellationToken).ConfigureAwait(false);
            var stagingName = SqlServerIdentifier.Normalize($"replicera_stage_{runId:N}");
            await ExecuteAsync(
                connection,
                transaction,
                SqlServerDmlBuilder.BuildCreateStaging(table, stagingName),
                cancellationToken).ConfigureAwait(false);
            if (syncType == "Initial")
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"DELETE FROM [dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))};",
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
        SqlConnection connection,
        string jobName,
        TableDefinition table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @tableId uniqueidentifier;
            SELECT @tableId = [TableId]
            FROM [replicera].[Tables]
            WHERE [ReplicationJobId] = @jobName AND [DataverseLogicalName] = @logicalName;

            IF @tableId IS NULL
            BEGIN
                SET @tableId = NEWID();
                INSERT INTO [replicera].[Tables]
                (
                    [TableId], [ReplicationJobId], [DataverseLogicalName], [DataverseEntitySetName],
                    [DestinationSchema], [DestinationTableName], [PrimaryKey], [ChangeTrackingEnabled], [Status]
                )
                VALUES
                (
                    @tableId, @jobName, @logicalName, @entitySetName,
                    N'dbo', @destinationName, @primaryKey, 1, N'Uninitialized'
                );
            END;

            SELECT @tableId;
            """;
        _ = command.Parameters.AddWithValue("@jobName", jobName);
        _ = command.Parameters.AddWithValue("@logicalName", table.LogicalName);
        _ = command.Parameters.AddWithValue("@entitySetName", table.EntitySetName);
        _ = command.Parameters.AddWithValue("@destinationName", SqlServerIdentifier.Normalize(table.DestinationName));
        _ = command.Parameters.AddWithValue("@primaryKey", table.PrimaryKey.LogicalName);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SQL Server did not return the managed table ID."));
    }

    private static async Task AcquireLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 0;
            SELECT @result;
            """;
        _ = command.Parameters.AddWithValue("@resource", $"replicera:{jobName}:{logicalName}");
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (result < 0)
        {
            throw new InvalidOperationException($"A synchronization is already running for job '{jobName}', table '{logicalName}'.");
        }
    }

    private static async Task CreateRunMetadataAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid runId,
        Guid tableId,
        string jobName,
        string syncType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [replicera].[Tables] SET [Status] = N'Syncing' WHERE [TableId] = @tableId;

            INSERT INTO [replicera].[SyncRuns]
                ([SyncRunId], [ReplicationJobId], [TableId], [SyncType], [StartedUtc], [Status])
            VALUES (@runId, @jobName, @tableId, @syncType, SYSUTCDATETIME(), N'Running');
            """;
        _ = command.Parameters.AddWithValue("@runId", runId);
        _ = command.Parameters.AddWithValue("@tableId", tableId);
        _ = command.Parameters.AddWithValue("@jobName", jobName);
        _ = command.Parameters.AddWithValue("@syncType", syncType);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
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

    private sealed class Session(
        SqlConnection connection,
        SqlTransaction transaction,
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

            var data = SqlServerBatchTable.Create(table, page);
            using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction))
            {
                bulkCopy.DestinationTableName = $"[dbo].{SqlServerIdentifier.Quote(stagingName)}";
                foreach (DataColumn column in data.Columns)
                {
                    _ = bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }

                await bulkCopy.WriteToServerAsync(data, cancellationToken).ConfigureAwait(false);
            }

            var result = await CountOperationsAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction,
                SqlServerDmlBuilder.BuildApplyStaging(table, stagingName),
                cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"TRUNCATE TABLE [dbo].{SqlServerIdentifier.Quote(stagingName)};", cancellationToken).ConfigureAwait(false);
            return result;
        }

        public async Task CommitAsync(
            string newCheckpoint,
            SyncMetrics metrics,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(committed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(newCheckpoint);
            await ExecuteAsync(
                connection,
                transaction,
                SqlServerDmlBuilder.BuildDropStaging(stagingName),
                cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE [replicera].[Tables]
                SET [ChangeCheckpoint] = @checkpoint,
                    [LastSuccessfulSyncUtc] = SYSUTCDATETIME(),
                    [LastIncrementalSyncUtc] = CASE WHEN @syncType = N'Incremental' THEN SYSUTCDATETIME() ELSE [LastIncrementalSyncUtc] END,
                    [LastFullSyncUtc] = CASE WHEN @syncType = N'Initial' THEN SYSUTCDATETIME() ELSE [LastFullSyncUtc] END,
                    [Status] = N'Healthy'
                WHERE [TableId] = @tableId;

                UPDATE [replicera].[SyncRuns]
                SET [CompletedUtc] = SYSUTCDATETIME(), [RecordsReceived] = @received,
                    [RecordsInserted] = @inserted, [RecordsUpdated] = @updated,
                    [RecordsDeleted] = @deleted, [PagesProcessed] = @pages, [Status] = N'Succeeded'
                WHERE [SyncRunId] = @runId;
                """;
            _ = command.Parameters.AddWithValue("@checkpoint", newCheckpoint);
            _ = command.Parameters.AddWithValue("@tableId", tableId);
            _ = command.Parameters.AddWithValue("@runId", runId);
            _ = command.Parameters.AddWithValue("@syncType", syncType);
            _ = command.Parameters.AddWithValue("@received", metrics.RecordsReceived);
            _ = command.Parameters.AddWithValue("@inserted", metrics.RecordsInserted);
            _ = command.Parameters.AddWithValue("@updated", metrics.RecordsUpdated);
            _ = command.Parameters.AddWithValue("@deleted", metrics.RecordsDeleted);
            _ = command.Parameters.AddWithValue("@pages", metrics.PagesProcessed);
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

        private async Task<PageApplyResult> CountOperationsAsync(CancellationToken cancellationToken)
        {
            var layout = SqlServerTableLayout.GetColumns(table);
            var primaryKey = layout.Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget).Name;
            var target = $"[dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))}";
            var staging = $"[dbo].{SqlServerIdentifier.Quote(stagingName)}";
            var key = SqlServerIdentifier.Quote(primaryKey);
            var operation = SqlServerIdentifier.Quote(SqlServerDmlBuilder.OperationColumn);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
#pragma warning disable CA2100 // SQL is generated solely from normalized and quoted provider-owned identifiers.
            command.CommandText = $"""
                SELECT
                    COALESCE(SUM(CASE WHEN source.{operation} = 'U' AND target.{key} IS NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN source.{operation} = 'U' AND target.{key} IS NOT NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN source.{operation} = 'D' AND target.{key} IS NOT NULL THEN 1 ELSE 0 END), 0)
                FROM {staging} AS source
                LEFT JOIN {target} AS target ON target.{key} = source.{key};
                """;
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new PageApplyResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }
    }
}
