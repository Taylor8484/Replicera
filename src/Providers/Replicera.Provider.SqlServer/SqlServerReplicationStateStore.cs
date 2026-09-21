using Microsoft.Data.SqlClient;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer;

public sealed class SqlServerReplicationStateStore(string connectionString) : IReplicationStateStore
{
    public async Task<TableReplicationState?> GetTableStateAsync(
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'[replicera].[Tables]', N'U') IS NULL
                RETURN;

            SELECT t.[Status], t.[ChangeCheckpoint], t.[LastSuccessfulSyncUtc],
                   latest.[SyncType], latest.[StartedUtc], latest.[CompletedUtc],
                   latest.[RecordsInserted], latest.[RecordsUpdated], latest.[RecordsDeleted],
                   latest.[ErrorCode], latest.[ErrorMessage], t.[LastSyncMode]
            FROM [replicera].[Tables] AS t
            OUTER APPLY
            (
                SELECT TOP (1) r.[SyncType], r.[StartedUtc], r.[CompletedUtc],
                       r.[RecordsInserted], r.[RecordsUpdated], r.[RecordsDeleted],
                       r.[ErrorCode], r.[ErrorMessage]
                FROM [replicera].[SyncRuns] AS r
                WHERE r.[TableId] = t.[TableId]
                ORDER BY r.[StartedUtc] DESC, r.[SyncRunId] DESC
            ) AS latest
            WHERE t.[ReplicationJobId] = @jobName AND t.[DataverseLogicalName] = @logicalName;
            """;
        _ = command.Parameters.AddWithValue("@jobName", jobName);
        _ = command.Parameters.AddWithValue("@logicalName", logicalName);
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
            reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDateTimeOffset(4),
            reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    public async Task MarkFailureAsync(
        string jobName,
        string logicalName,
        TableState state,
        string errorCode,
        string sanitizedMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @tableId uniqueidentifier;
            DECLARE @runId uniqueidentifier;
            SELECT @tableId = [TableId]
            FROM [replicera].[Tables]
            WHERE [ReplicationJobId] = @jobName AND [DataverseLogicalName] = @logicalName;

            IF @tableId IS NULL
                RETURN;

            UPDATE [replicera].[Tables] SET [Status] = @status WHERE [TableId] = @tableId;

            SELECT TOP (1) @runId = [SyncRunId]
            FROM [replicera].[SyncRuns]
            WHERE [TableId] = @tableId AND [Status] = N'Running'
            ORDER BY [StartedUtc] DESC;

            UPDATE [replicera].[SyncRuns]
            SET [CompletedUtc] = SYSUTCDATETIME(), [Status] = N'Failed',
                [ErrorCode] = @errorCode, [ErrorMessage] = @message
            WHERE [SyncRunId] = @runId;

            IF @runId IS NULL
            BEGIN
                INSERT INTO [replicera].[SyncRuns]
                    ([SyncRunId], [ReplicationJobId], [TableId], [SyncType], [StartedUtc], [CompletedUtc],
                     [Status], [ErrorCode], [ErrorMessage])
                VALUES
                    (NEWID(), @jobName, @tableId, N'Unknown', SYSUTCDATETIME(), SYSUTCDATETIME(),
                     N'Failed', @errorCode, @message);
            END;
            """;
        _ = command.Parameters.AddWithValue("@jobName", jobName);
        _ = command.Parameters.AddWithValue("@logicalName", logicalName);
        _ = command.Parameters.AddWithValue("@status", state.ToString());
        _ = command.Parameters.AddWithValue("@errorCode", errorCode);
        _ = command.Parameters.AddWithValue("@message", sanitizedMessage[..Math.Min(sanitizedMessage.Length, 2048)]);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
