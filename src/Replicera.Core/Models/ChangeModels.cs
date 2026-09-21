namespace Replicera.Core.Models;

public enum ChangeKind
{
    Upsert,
    Delete
}

public sealed record SourceRecord(
    Guid Id,
    ChangeKind Kind,
    IReadOnlyDictionary<string, object?> Values);

public sealed record LookupValue(Guid Id, string TargetLogicalName);

public sealed record ChoiceSetValue(IReadOnlyList<int> Values);

public sealed record SourcePage(
    IReadOnlyList<SourceRecord> Records,
    string? ContinuationToken,
    string? DataCheckpoint,
    bool HasMoreRecords);

public enum TableState
{
    Uninitialized,
    Initializing,
    Healthy,
    Syncing,
    Failed,
    Blocked,
    ResyncRequired,
    SchemaConflict,
    SourceRemoved
}

public sealed record SyncMetrics(
    long PagesProcessed,
    long RecordsReceived,
    long RecordsInserted,
    long RecordsUpdated,
    long RecordsDeleted);
