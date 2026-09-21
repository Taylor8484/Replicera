using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;

namespace Replicera.Core.Replication;

public sealed partial class ReplicationEngine
{
    private readonly ISourceChangeReader source;
    private readonly IDestinationWriter destination;
    private readonly IReplicationStateStore stateStore;
    private readonly ILogger<ReplicationEngine> logger;

    public ReplicationEngine(
        ISourceChangeReader source,
        IDestinationWriter destination,
        IReplicationStateStore stateStore,
        ILogger<ReplicationEngine>? logger = null)
    {
        this.source = source;
        this.destination = destination;
        this.stateStore = stateStore;
        this.logger = logger ?? NullLogger<ReplicationEngine>.Instance;
    }

    public async Task<SyncMetrics> SyncAsync(
        string jobName,
        TableDefinition table,
        int pageSize,
        CancellationToken cancellationToken,
        bool forceInitial = false,
        bool preserveExisting = false,
        bool retainDeletedRows = false,
        SynchronizationMode mode = SynchronizationMode.Complete,
        bool externalLockHeld = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(table);

        var state = await stateStore.GetTableStateAsync(
            jobName,
            table.LogicalName,
            cancellationToken).ConfigureAwait(false);

        var modeRequiresFullSync = string.Equals(
            state?.LastSyncMode,
            SynchronizationMode.NoDataLoss.ToString(),
            StringComparison.OrdinalIgnoreCase)
            && mode == SynchronizationMode.Complete;
        var currentCheckpoint = forceInitial || modeRequiresFullSync ? null : state?.DataCheckpoint;
        var syncType = currentCheckpoint is null ? "initial" : "incremental";
        LogReplicationStarted(logger, jobName, table.LogicalName, syncType);
        try
        {
            await using var session = currentCheckpoint is null
                ? await destination.BeginInitialSyncAsync(jobName, table, cancellationToken, !preserveExisting, retainDeletedRows, mode, externalLockHeld).ConfigureAwait(false)
                : await destination.BeginIncrementalSyncAsync(jobName, table, currentCheckpoint, cancellationToken, retainDeletedRows, mode, externalLockHeld).ConfigureAwait(false);

            long pages = 0;
            long received = 0;
            long inserted = 0;
            long updated = 0;
            long deleted = 0;
            string? terminalCheckpoint = null;

            await foreach (var page in source.ReadChangesAsync(
                               table,
                               currentCheckpoint,
                               pageSize,
                               cancellationToken).ConfigureAwait(false))
            {
                if (terminalCheckpoint is not null)
                {
                    throw new RepliceraException(
                        ErrorCategory.Synchronization,
                        "The source returned records after its terminal checkpoint.");
                }

                var applied = await session.ApplyPageAsync(page, cancellationToken).ConfigureAwait(false);
                pages++;
                received += page.Records.Count;
                inserted += applied.Inserted;
                updated += applied.Updated;
                deleted += applied.Deleted;
                terminalCheckpoint = page.DataCheckpoint;
                LogPageApplied(
                    logger,
                    pages,
                    jobName,
                    table.LogicalName,
                    page.Records.Count,
                    applied.Inserted,
                    applied.Updated,
                    applied.Deleted);
            }

            if (terminalCheckpoint is null)
            {
                throw new RepliceraException(
                    ErrorCategory.Synchronization,
                    "The source completed without returning a checkpoint.");
            }

            var metrics = new SyncMetrics(pages, received, inserted, updated, deleted);
            await session.CommitAsync(terminalCheckpoint, metrics, cancellationToken).ConfigureAwait(false);
            LogReplicationCommitted(
                logger,
                jobName,
                table.LogicalName,
                pages,
                received,
                inserted,
                updated,
                deleted);
            return metrics;
        }
        catch (RepliceraException exception)
        {
            LogReplicationFailed(
                logger,
                jobName,
                table.LogicalName,
                exception.Category);
            var failedState = exception.Category == ErrorCategory.ExpiredCheckpoint
                ? TableState.ResyncRequired
                : TableState.Failed;
            await TryMarkFailureAsync(
                jobName,
                table.LogicalName,
                failedState,
                exception.Category.ToString(),
                FailureMessage(exception),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            LogReplicationCancelled(
                logger,
                jobName,
                table.LogicalName);
            await TryMarkFailureAsync(
                jobName,
                table.LogicalName,
                TableState.Failed,
                ErrorCategory.Synchronization.ToString(),
                "Synchronization was cancelled before the checkpoint could be committed.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReplicationFailedUnexpectedly(
                logger,
                jobName,
                table.LogicalName);
            await TryMarkFailureAsync(
                jobName,
                table.LogicalName,
                TableState.Failed,
                ErrorCategory.Synchronization.ToString(),
                "Synchronization failed before the checkpoint could be committed.",
                CancellationToken.None).ConfigureAwait(false);
            throw new RepliceraException(
                ErrorCategory.Synchronization,
                "Synchronization failed before the checkpoint could be committed.",
                exception);
        }
    }

    private static string FailureMessage(RepliceraException exception) => exception.Category switch
    {
        ErrorCategory.ExpiredCheckpoint => "The source checkpoint is no longer valid; a full resynchronization is required.",
        _ => exception.Message
    };

    private async Task TryMarkFailureAsync(
        string jobName,
        string logicalName,
        TableState state,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await stateStore.MarkFailureAsync(
                jobName,
                logicalName,
                state,
                errorCode,
                message,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Failure reporting must not replace the synchronization exception.
        }
    }

    [LoggerMessage(
        EventId = 1000,
        EventName = "ReplicationStarted",
        Level = LogLevel.Information,
        Message = "Replication started for job {Job}, table {Table}, mode {SyncType}")]
    private static partial void LogReplicationStarted(ILogger logger, string job, string table, string syncType);

    [LoggerMessage(
        EventId = 1001,
        EventName = "PageApplied",
        Level = LogLevel.Information,
        Message = "Applied page {PageNumber} for job {Job}, table {Table}: {RecordsReceived} received, {RecordsInserted} inserted, {RecordsUpdated} updated, {RecordsDeleted} deleted")]
    private static partial void LogPageApplied(
        ILogger logger,
        long pageNumber,
        string job,
        string table,
        int recordsReceived,
        long recordsInserted,
        long recordsUpdated,
        long recordsDeleted);

    [LoggerMessage(
        EventId = 1002,
        EventName = "ReplicationCommitted",
        Level = LogLevel.Information,
        Message = "Replication committed for job {Job}, table {Table}: {PagesProcessed} pages, {RecordsReceived} received, {RecordsInserted} inserted, {RecordsUpdated} updated, {RecordsDeleted} deleted")]
    private static partial void LogReplicationCommitted(
        ILogger logger,
        string job,
        string table,
        long pagesProcessed,
        long recordsReceived,
        long recordsInserted,
        long recordsUpdated,
        long recordsDeleted);

    [LoggerMessage(
        EventId = 1003,
        EventName = "ReplicationFailed",
        Level = LogLevel.Error,
        Message = "Replication failed for job {Job}, table {Table}, category {ErrorCategory}")]
    private static partial void LogReplicationFailed(
        ILogger logger,
        string job,
        string table,
        ErrorCategory errorCategory);

    [LoggerMessage(
        EventId = 1004,
        EventName = "ReplicationCancelled",
        Level = LogLevel.Warning,
        Message = "Replication cancelled for job {Job}, table {Table}")]
    private static partial void LogReplicationCancelled(ILogger logger, string job, string table);

    [LoggerMessage(
        EventId = 1005,
        EventName = "ReplicationFailedUnexpectedly",
        Level = LogLevel.Error,
        Message = "Replication failed unexpectedly for job {Job}, table {Table}")]
    private static partial void LogReplicationFailedUnexpectedly(ILogger logger, string job, string table);
}
