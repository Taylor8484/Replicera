using System.Diagnostics;
using System.Runtime.CompilerServices;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;
using Replicera.Core.Replication;
using Xunit.Abstractions;

namespace Replicera.IntegrationTests;

public sealed class ScaleIntegrationTests(ITestOutputHelper output)
{
    private const int RecordCount = 65_536;
    private const int PayloadCharacters = 4_096;
    private const int PageSize = 256;
    private const double MinimumRecordsPerSecond = 5_000;

    [Fact]
    [Trait("Category", "Scale")]
    public async Task StreamingDataset_LargerThanHeap_RemainsBoundedAndMeetsBaselineThroughput()
    {
        var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        const long expectedHeapLimit = 256L * 1024 * 1024;
        Assert.InRange(availableMemory, expectedHeapLimit / 2, expectedHeapLimit * 2);

        var session = new MeasuringSession();
        var engine = new ReplicationEngine(
            new GeneratedSource(),
            new SingleSessionDestination(session),
            new EmptyStateStore());
        var stopwatch = Stopwatch.StartNew();

        var metrics = await engine.SyncAsync("scale", Table(), PageSize, CancellationToken.None);

        stopwatch.Stop();
        var logicalBytes = (long)RecordCount * PayloadCharacters * sizeof(char);
        var recordsPerSecond = RecordCount / stopwatch.Elapsed.TotalSeconds;
        output.WriteLine(
            $"Logical dataset: {logicalBytes / 1024d / 1024d:F1} MiB; " +
            $"available managed memory: {availableMemory / 1024d / 1024d:F1} MiB; " +
            $"peak live managed memory: {session.PeakManagedBytes / 1024d / 1024d:F1} MiB; " +
            $"throughput: {recordsPerSecond:F0} records/s.");

        Assert.True(logicalBytes > availableMemory);
        Assert.True(session.PeakManagedBytes < availableMemory);
        Assert.True(recordsPerSecond >= MinimumRecordsPerSecond);
        Assert.Equal(RecordCount, metrics.RecordsReceived);
        Assert.Equal(RecordCount, metrics.RecordsInserted);
        Assert.Equal(RecordCount / PageSize, metrics.PagesProcessed);
    }

    private static TableDefinition Table() => new(
        "account",
        "accounts",
        "account",
        [
            new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new ColumnDefinition { LogicalName = "description", SourceType = SourceType.String, MaxLength = PayloadCharacters }
        ]);

    private sealed class GeneratedSource : ISourceChangeReader
    {
        public async IAsyncEnumerable<SourcePage> ReadChangesAsync(
            TableDefinition table,
            string? dataCheckpoint,
            int pageSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < RecordCount; offset += pageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(pageSize, RecordCount - offset);
                var records = new SourceRecord[count];
                for (var index = 0; index < count; index++)
                {
                    var ordinal = offset + index;
                    records[index] = new SourceRecord(
                        Guid.NewGuid(),
                        ChangeKind.Upsert,
                        new Dictionary<string, object?>
                        {
                            ["description"] = ordinal.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) +
                                new string('x', PayloadCharacters - 8)
                        });
                }

                var hasMore = offset + count < RecordCount;
                yield return new SourcePage(
                    records,
                    hasMore ? $"page-{offset / pageSize + 1}" : null,
                    hasMore ? null : "terminal-checkpoint",
                    hasMore);
                await Task.Yield();
            }
        }
    }

    private sealed class SingleSessionDestination(MeasuringSession session) : IDestinationWriter
    {
        public Task<IReplicationSession> BeginInitialSyncAsync(
            string jobName,
            TableDefinition table,
            CancellationToken cancellationToken) => Task.FromResult<IReplicationSession>(session);

        public Task<IReplicationSession> BeginIncrementalSyncAsync(
            string jobName,
            TableDefinition table,
            string currentCheckpoint,
            CancellationToken cancellationToken) => Task.FromResult<IReplicationSession>(session);
    }

    private sealed class MeasuringSession : IReplicationSession
    {
        public long PeakManagedBytes { get; private set; }

        public Task<PageApplyResult> ApplyPageAsync(SourcePage page, CancellationToken cancellationToken)
        {
            PeakManagedBytes = Math.Max(PeakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
            return Task.FromResult(new PageApplyResult(page.Records.Count, 0, 0));
        }

        public Task CommitAsync(string newCheckpoint, SyncMetrics metrics, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyStateStore : IReplicationStateStore
    {
        public Task<TableReplicationState?> GetTableStateAsync(
            string jobName,
            string logicalName,
            CancellationToken cancellationToken) => Task.FromResult<TableReplicationState?>(null);

        public Task MarkFailureAsync(
            string jobName,
            string logicalName,
            TableState state,
            string errorCode,
            string sanitizedMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
