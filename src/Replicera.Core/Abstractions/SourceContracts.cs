using Replicera.Core.Models;

namespace Replicera.Core.Abstractions;

public interface ISourceConnection
{
    Task TestConnectionAsync(CancellationToken cancellationToken);
}

public interface ISourceMetadataReader
{
    Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken cancellationToken);

    Task<TableDefinition> GetTableAsync(string logicalName, CancellationToken cancellationToken);

    Task<SourceTablePresence> ConfirmTablePresenceAsync(string logicalName, CancellationToken cancellationToken);
}

public enum SourceTablePresence
{
    Present,
    Missing
}

public interface ISourceChangeReader
{
    IAsyncEnumerable<SourcePage> ReadChangesAsync(
        TableDefinition table,
        string? dataCheckpoint,
        int pageSize,
        CancellationToken cancellationToken);
}

public interface IChangeTrackingManager
{
    Task<ChangeTrackingStatus> GetStatusAsync(string logicalName, CancellationToken cancellationToken);

    Task EnableAsync(string logicalName, CancellationToken cancellationToken);
}

public sealed record ChangeTrackingStatus(bool IsEnabled, bool CanEnable, string? BlockedReason);
