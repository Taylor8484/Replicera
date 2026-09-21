using Replicera.Core.Configuration;
using Replicera.Core.Models;
using Replicera.Core.Schema;

namespace Replicera.Core.Abstractions;

public interface IDestinationProvider
{
    string ProviderName { get; }

    string DefaultSchema { get; }

    IDestinationConnection CreateConnection(string connectionString);

    IDestinationSchemaManager CreateSchemaManager(string connectionString);

    IDestinationWriter CreateWriter(string connectionString);

    IReplicationStateStore CreateStateStore(string connectionString);

    Task<IAsyncDisposable> AcquireTableLockAsync(
        string connectionString,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken);

    Task EnsureMetadataStoreAsync(string connectionString, CancellationToken cancellationToken);
}

public interface IDestinationConnection
{
    Task TestConnectionAsync(CancellationToken cancellationToken);
}

public interface IDestinationSchemaManager
{
    Task<DestinationTable?> ReadTableAsync(TableDefinition source, CancellationToken cancellationToken);

    Task ApplySchemaPlanAsync(
        string jobName,
        TableDefinition source,
        SchemaPlan plan,
        CancellationToken cancellationToken);

    Task<bool> ReconcileMissingTableAsync(
        string jobName,
        string logicalName,
        SynchronizationMode mode,
        CancellationToken cancellationToken);
}

public interface IReplicationSession : IAsyncDisposable
{
    Task<PageApplyResult> ApplyPageAsync(SourcePage page, CancellationToken cancellationToken);

    Task CommitAsync(string newCheckpoint, SyncMetrics metrics, CancellationToken cancellationToken);
}

public sealed record PageApplyResult(long Inserted, long Updated, long Deleted);

public interface IDestinationWriter
{
    Task<IReplicationSession> BeginInitialSyncAsync(
        string jobName,
        TableDefinition table,
        CancellationToken cancellationToken,
        bool replaceExisting = true,
        bool retainDeletedRows = false,
        SynchronizationMode mode = SynchronizationMode.Complete,
        bool externalLockHeld = false);

    Task<IReplicationSession> BeginIncrementalSyncAsync(
        string jobName,
        TableDefinition table,
        string currentCheckpoint,
        CancellationToken cancellationToken,
        bool retainDeletedRows = false,
        SynchronizationMode mode = SynchronizationMode.Complete,
        bool externalLockHeld = false);
}

public interface IReplicationStateStore
{
    Task<TableReplicationState?> GetTableStateAsync(
        string jobName,
        string logicalName,
        CancellationToken cancellationToken);

    Task MarkFailureAsync(
        string jobName,
        string logicalName,
        TableState state,
        string errorCode,
        string sanitizedMessage,
        CancellationToken cancellationToken);
}

public sealed record TableReplicationState(
    string JobName,
    string LogicalName,
    TableState State,
    string? DataCheckpoint,
    DateTimeOffset? LastSuccessfulSyncUtc,
    string? LastRunType = null,
    DateTimeOffset? LastRunStartedUtc = null,
    DateTimeOffset? LastRunCompletedUtc = null,
    long? LastRunRecordsInserted = null,
    long? LastRunRecordsUpdated = null,
    long? LastRunRecordsDeleted = null,
    string? LastErrorCode = null,
    string? LastErrorMessage = null,
    string? LastSyncMode = null);
