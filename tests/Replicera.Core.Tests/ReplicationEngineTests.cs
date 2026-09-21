using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Core.Replication;

namespace Replicera.Core.Tests;

public sealed class ReplicationEngineTests
{
    [Fact]
    public async Task SyncAsync_CommitsOnlyTerminalCheckpointAndAccumulatesMetrics()
    {
        var pages = new[]
        {
            Page(2, true, null),
            Page(1, false, "opaque-token")
        };
        var source = new FakeSource(pages);
        var session = new FakeSession();
        var destination = new FakeDestination(session);
        var state = new FakeStateStore(null);
        var engine = new ReplicationEngine(source, destination, state);

        var metrics = await engine.SyncAsync("job", Table(), 100, CancellationToken.None);

        Assert.True(destination.BeganInitial);
        Assert.Equal("opaque-token", session.CommittedCheckpoint);
        Assert.Equal(3, metrics.RecordsReceived);
        Assert.Equal(2, metrics.RecordsInserted);
        Assert.Equal(1, metrics.RecordsDeleted);
    }

    [Fact]
    public async Task SyncAsync_EmitsStructuredEventsWithoutCheckpointValues()
    {
        const string checkpoint = "sensitive-opaque-token";
        var logger = new CapturingLogger();
        var engine = new ReplicationEngine(
            new FakeSource([Page(1, false, checkpoint)]),
            new FakeDestination(new FakeSession()),
            new FakeStateStore(null),
            logger);

        _ = await engine.SyncAsync("job", Table(), 100, CancellationToken.None);

        Assert.Equal(
            ["ReplicationStarted", "PageApplied", "ReplicationCommitted"],
            logger.Events.Select(logEvent => logEvent.EventName));
        Assert.All(logger.Events, logEvent => Assert.DoesNotContain(checkpoint, logEvent.Content, StringComparison.Ordinal));
        Assert.Contains(logger.Events, logEvent => logEvent.Content.Contains("Job=job", StringComparison.Ordinal));
        Assert.Contains(logger.Events, logEvent => logEvent.Content.Contains("Table=account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncAsync_DoesNotCommitWhenSourceHasNoCheckpoint()
    {
        var session = new FakeSession();
        var engine = new ReplicationEngine(
            new FakeSource([Page(1, true, null)]),
            new FakeDestination(session),
            new FakeStateStore(null));

        await Assert.ThrowsAsync<RepliceraException>(
            () => engine.SyncAsync("job", Table(), 100, CancellationToken.None));

        Assert.Null(session.CommittedCheckpoint);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task SyncAsync_MarksExpiredCheckpointForResync()
    {
        var state = new FakeStateStore(
            new TableReplicationState("job", "account", TableState.Healthy, "old-token", null));
        var engine = new ReplicationEngine(
            new ThrowingSource(new RepliceraException(ErrorCategory.ExpiredCheckpoint, "Token expired.")),
            new FakeDestination(new FakeSession()),
            state);

        await Assert.ThrowsAsync<RepliceraException>(
            () => engine.SyncAsync("job", Table(), 100, CancellationToken.None));

        Assert.Equal(TableState.ResyncRequired, state.MarkedState);
        Assert.Equal("ExpiredCheckpoint", state.ErrorCode);
        Assert.DoesNotContain("Token expired", state.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_RollsBackAndMarksUnexpectedWriteFailure()
    {
        var session = new FakeSession { ApplyException = new InvalidOperationException("sensitive-row-payload") };
        var state = new FakeStateStore(null);
        var engine = new ReplicationEngine(
            new FakeSource([Page(1, false, "new-token")]),
            new FakeDestination(session),
            state);

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => engine.SyncAsync("job", Table(), 100, CancellationToken.None));

        Assert.Equal(ErrorCategory.Synchronization, exception.Category);
        Assert.Null(session.CommittedCheckpoint);
        Assert.True(session.Disposed);
        Assert.Equal(TableState.Failed, state.MarkedState);
        Assert.DoesNotContain("sensitive-row-payload", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-row-payload", state.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_CancellationMarksFailureWithoutCommitting()
    {
        var session = new FakeSession();
        var state = new FakeStateStore(null);
        var engine = new ReplicationEngine(
            new ThrowingSource(new OperationCanceledException()),
            new FakeDestination(session),
            state);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.SyncAsync("job", Table(), 100, CancellationToken.None));

        Assert.Null(session.CommittedCheckpoint);
        Assert.True(session.Disposed);
        Assert.Equal(TableState.Failed, state.MarkedState);
        Assert.Equal("Synchronization", state.ErrorCode);
    }

    [Fact]
    public async Task SyncAsync_CommitFailureMarksFailureAndDoesNotAcceptCheckpoint()
    {
        var session = new FakeSession { CommitException = new InvalidOperationException("Injected commit failure.") };
        var state = new FakeStateStore(null);
        var engine = new ReplicationEngine(
            new FakeSource([Page(1, false, "uncommitted-token")]),
            new FakeDestination(session),
            state);

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => engine.SyncAsync("job", Table(), 100, CancellationToken.None));

        Assert.Equal(ErrorCategory.Synchronization, exception.Category);
        Assert.Null(session.CommittedCheckpoint);
        Assert.True(session.Disposed);
        Assert.Equal(TableState.Failed, state.MarkedState);
    }

    [Fact]
    public async Task SyncAsync_ForceInitialIgnoresExistingCheckpoint()
    {
        var session = new FakeSession();
        var destination = new FakeDestination(session);
        var engine = new ReplicationEngine(
            new FakeSource([Page(0, false, "replacement-token")]),
            destination,
            new FakeStateStore(new TableReplicationState("job", "account", TableState.Healthy, "old-token", null)));

        _ = await engine.SyncAsync("job", Table(), 100, CancellationToken.None, forceInitial: true);

        Assert.True(destination.BeganInitial);
        Assert.False(destination.BeganIncremental);
        Assert.Equal("replacement-token", session.CommittedCheckpoint);
    }

    [Fact]
    public async Task SyncAsync_NoDataLossUsesFullMergeAndRetainsDeletedRows()
    {
        var session = new FakeSession();
        var destination = new FakeDestination(session);
        var engine = new ReplicationEngine(
            new FakeSource([Page(2, false, "replacement-token")]),
            destination,
            new FakeStateStore(new TableReplicationState("job", "account", TableState.Healthy, "old-token", null)));

        var metrics = await engine.SyncAsync(
            "job",
            Table(),
            100,
            CancellationToken.None,
            forceInitial: true,
            preserveExisting: true,
            retainDeletedRows: true,
            mode: SynchronizationMode.NoDataLoss);

        Assert.True(destination.BeganInitial);
        Assert.False(destination.ReplaceExisting);
        Assert.True(destination.RetainDeletedRows);
        Assert.Equal(2, metrics.RecordsReceived);
        Assert.Equal(1, metrics.RecordsInserted);
        Assert.Equal(1, metrics.RecordsDeleted);
    }

    [Fact]
    public async Task SyncAsync_SwitchingFromNoDataLossToCompleteForcesReplacement()
    {
        var source = new FakeSource([Page(0, false, "replacement-token")]);
        var destination = new FakeDestination(new FakeSession());
        var state = new TableReplicationState(
            "job",
            "account",
            TableState.Healthy,
            "old-token",
            null,
            LastSyncMode: SynchronizationMode.NoDataLoss.ToString());
        var engine = new ReplicationEngine(source, destination, new FakeStateStore(state));

        _ = await engine.SyncAsync(
            "job",
            Table(),
            100,
            CancellationToken.None,
            mode: SynchronizationMode.Complete);

        Assert.True(destination.BeganInitial);
        Assert.True(destination.ReplaceExisting);
        Assert.Null(source.LastCheckpoint);
    }

    private static SourcePage Page(int records, bool more, string? checkpoint)
    {
        var values = Enumerable.Range(0, records)
            .Select(index => new SourceRecord(
                Guid.NewGuid(),
                index == records - 1 && checkpoint is not null ? ChangeKind.Delete : ChangeKind.Upsert,
                new Dictionary<string, object?>()))
            .ToArray();
        return new SourcePage(values, more ? "next" : null, checkpoint, more);
    }

    private static TableDefinition Table() => new(
        "account",
        "accounts",
        "account",
        [new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true }]);

    private sealed class FakeSource(IEnumerable<SourcePage> pages) : ISourceChangeReader
    {
        public string? LastCheckpoint { get; private set; }

        public async IAsyncEnumerable<SourcePage> ReadChangesAsync(
            TableDefinition table,
            string? dataCheckpoint,
            int pageSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastCheckpoint = dataCheckpoint;
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return page;
                await Task.Yield();
            }
        }
    }

    private sealed class CapturingLogger : ILogger<ReplicationEngine>
    {
        public List<(string? EventName, string Content)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? string.Join(",", values.Select(value => $"{value.Key}={value.Value}"))
                : formatter(state, exception);
            Events.Add((eventId.Name, properties));
        }
    }

    private sealed class ThrowingSource(Exception exception) : ISourceChangeReader
    {
        public async IAsyncEnumerable<SourcePage> ReadChangesAsync(
            TableDefinition table,
            string? dataCheckpoint,
            int pageSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FakeDestination(FakeSession session) : IDestinationWriter
    {
        public bool BeganInitial { get; private set; }

        public bool BeganIncremental { get; private set; }

        public bool ReplaceExisting { get; private set; }

        public bool RetainDeletedRows { get; private set; }

        public Task<IReplicationSession> BeginInitialSyncAsync(
            string jobName,
            TableDefinition table,
            CancellationToken cancellationToken,
            bool replaceExisting = true,
            bool retainDeletedRows = false,
            SynchronizationMode mode = SynchronizationMode.Complete,
            bool externalLockHeld = false)
        {
            BeganInitial = true;
            ReplaceExisting = replaceExisting;
            RetainDeletedRows = retainDeletedRows;
            return Task.FromResult<IReplicationSession>(session);
        }

        public Task<IReplicationSession> BeginIncrementalSyncAsync(
            string jobName,
            TableDefinition table,
            string currentCheckpoint,
            CancellationToken cancellationToken,
            bool retainDeletedRows = false,
            SynchronizationMode mode = SynchronizationMode.Complete,
            bool externalLockHeld = false)
        {
            BeganIncremental = true;
            return Task.FromResult<IReplicationSession>(session);
        }
    }

    private sealed class FakeSession : IReplicationSession
    {
        public Exception? ApplyException { get; init; }

        public Exception? CommitException { get; init; }

        public string? CommittedCheckpoint { get; private set; }

        public bool Disposed { get; private set; }

        public Task<PageApplyResult> ApplyPageAsync(SourcePage page, CancellationToken cancellationToken)
        {
            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            var deleted = page.Records.Count(record => record.Kind == ChangeKind.Delete);
            return Task.FromResult(new PageApplyResult(page.Records.Count - deleted, 0, deleted));
        }

        public Task CommitAsync(string newCheckpoint, SyncMetrics metrics, CancellationToken cancellationToken)
        {
            if (CommitException is not null)
            {
                throw CommitException;
            }

            CommittedCheckpoint = newCheckpoint;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStateStore(TableReplicationState? state) : IReplicationStateStore
    {
        public TableState? MarkedState { get; private set; }

        public string? ErrorCode { get; private set; }

        public string? Message { get; private set; }

        public Task<TableReplicationState?> GetTableStateAsync(
            string jobName,
            string logicalName,
            CancellationToken cancellationToken) => Task.FromResult(state);

        public Task MarkFailureAsync(
            string jobName,
            string logicalName,
            TableState failedState,
            string errorCode,
            string sanitizedMessage,
            CancellationToken cancellationToken)
        {
            MarkedState = failedState;
            ErrorCode = errorCode;
            Message = sanitizedMessage;
            return Task.CompletedTask;
        }
    }
}
