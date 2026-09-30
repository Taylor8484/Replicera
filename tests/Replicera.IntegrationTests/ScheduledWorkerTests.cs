using System.Text.Json;
using Replicera.Cli;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;

namespace Replicera.IntegrationTests;

public sealed class ScheduledWorkerTests
{
    private static readonly ScheduleConfiguration FiveMinutes = new() { Interval = TimeSpan.FromMinutes(5) };

    [Fact]
    public async Task RunAsync_AppliesReloadedIntervalToTheNextWait()
    {
        var harness = new Harness(
            Scheduled(FiveMinutes),
            Scheduled(FiveMinutes with { Interval = TimeSpan.FromMinutes(10) }));

        var exitCode = await harness.RunAsync(FiveMinutes, stopAfterSyncs: 2);

        Assert.Equal(0, exitCode);
        Assert.Equal(["sync", "delay 00:05:00", "sync"], harness.Steps);
    }

    [Fact]
    public async Task RunAsync_IdlesWhileScheduleIsDisabledAndResumesWhenEnabled()
    {
        var harness = new Harness(
            Scheduled(FiveMinutes),
            Scheduled(FiveMinutes with { Enabled = false }),
            Scheduled(null),
            Scheduled(FiveMinutes));

        var exitCode = await harness.RunAsync(FiveMinutes, stopAfterSyncs: 2);

        Assert.Equal(0, exitCode);
        Assert.Equal(["sync", "delay 00:05:00", "delay 00:01:00", "delay 00:01:00", "sync"], harness.Steps);
        Assert.Equal(
            ["workerStarted", "syncStarted", "syncSucceeded", "scheduleDisabled", "scheduleEnabled", "syncStarted", "syncSucceeded", "workerStopped"],
            harness.Events);
    }

    [Fact]
    public async Task RunAsync_StartsIdleWhenScheduleIsDisabled()
    {
        var disabled = FiveMinutes with { Enabled = false };
        var harness = new Harness(Scheduled(disabled), Scheduled(FiveMinutes));

        var exitCode = await harness.RunAsync(disabled, stopAfterSyncs: 1);

        Assert.Equal(0, exitCode);
        Assert.Equal(["delay 00:01:00", "sync"], harness.Steps);
        Assert.Equal("scheduleDisabled", harness.Events[1]);
    }

    [Fact]
    public async Task RunAsync_StopsWithInvalidInputWhenJobIsRemoved()
    {
        var harness = new Harness(Scheduled(FiveMinutes), new WorkerScheduleState(null, false));

        var exitCode = await harness.RunAsync(FiveMinutes, stopAfterSyncs: 10);

        Assert.Equal((int)ExitCode.InvalidInput, exitCode);
        Assert.Equal(["sync", "delay 00:05:00"], harness.Steps);
        var stopped = harness.EventDocuments[^1];
        Assert.Equal("workerStopped", stopped.GetProperty("eventName").GetString());
        Assert.Equal(2, stopped.GetProperty("exitCode").GetInt32());
        Assert.Contains("no longer exists", stopped.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_KeepsLastValidScheduleWhenConfigurationIsInvalid()
    {
        var harness = new Harness(
            Scheduled(FiveMinutes with { Interval = TimeSpan.FromMinutes(7) }),
            new RepliceraException(ErrorCategory.Configuration, "jobs[0].tables: At least one table is required."));

        var exitCode = await harness.RunAsync(FiveMinutes, stopAfterSyncs: 2);

        Assert.Equal(0, exitCode);
        Assert.Equal(["sync", "delay 00:07:00", "sync"], harness.Steps);
        var invalid = Assert.Single(harness.EventDocuments, document => document.GetProperty("eventName").GetString() == "configurationInvalid");
        Assert.Contains("At least one table is required.", invalid.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(300, 0, 0.9, 300)]
    [InlineData(300, 1, 0.5, 300)]
    [InlineData(300, 2, 0.5, 600)]
    [InlineData(300, 3, 0.5, 1200)]
    [InlineData(300, 4, 0.5, 2400)]
    [InlineData(300, 5, 0.5, 3600)]
    [InlineData(300, 2, 0.0, 480)]
    [InlineData(300, 2, 1.0, 720)]
    [InlineData(1, 40, 0.5, 3600)]
    [InlineData(7200, 3, 0.5, 7200)]
    public void NextRunDelay_DoublesAfterEachFailureUpToCeiling(int intervalSeconds, int failures, double jitter, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            ScheduledWorker.NextRunDelay(TimeSpan.FromSeconds(intervalSeconds), failures, jitter));
    }

    [Fact]
    public async Task RunAsync_BacksOffAfterConsecutiveFailuresAndResetsAfterSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var exitCodes = new Queue<int>([3, 3, 0, 3]);
        var delays = new List<TimeSpan>();

        await ScheduledWorker.RunAsync(
            "job",
            FiveMinutes,
            _ =>
            {
                var exitCode = exitCodes.Dequeue();
                if (exitCodes.Count == 0)
                {
                    cancellation.Cancel();
                }

                return Task.FromResult(new WorkerSyncResult(exitCode, exitCode == 0 ? null : "Dataverse rejected the configured identity or authentication token."));
            },
            output,
            true,
            cancellation.Token,
            (duration, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays.Add(duration);
                return Task.CompletedTask;
            },
            jitter: () => 0.5);

        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5)], delays);
        var results = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(document => document.GetProperty("eventName").GetString() is "syncFailed" or "syncSucceeded")
            .Select(document => (document.GetProperty("consecutiveFailures").GetInt32(), document.GetProperty("nextRunIn").GetString()))
            .ToList();
        Assert.Equal([(1, "00:05:00"), (2, "00:10:00"), (0, "00:05:00"), (1, "00:05:00")], results);
        var failed = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .First(document => document.GetProperty("eventName").GetString() == "syncFailed");
        Assert.Equal(3, failed.GetProperty("exitCode").GetInt32());
        Assert.True(failed.GetProperty("requiresAttention").GetBoolean());
        Assert.Equal("Dataverse rejected the configured identity or authentication token.", failed.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(70, false)]
    public void WorkerSyncResult_FlagsFailuresThatNeedAnOperator(int exitCode, bool expected)
    {
        Assert.Equal(expected, new WorkerSyncResult(exitCode).RequiresAttention);
    }

    private static WorkerScheduleState Scheduled(ScheduleConfiguration? schedule) => new(schedule, true);

    private sealed class Harness(params object[] reloads)
    {
        private readonly Queue<object> reloads = new(reloads);
        private string output = string.Empty;

        public List<string> Steps { get; } = [];

        public List<JsonElement> EventDocuments => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        public List<string> Events => EventDocuments.Select(document => document.GetProperty("eventName").GetString()!).ToList();

        public async Task<int> RunAsync(ScheduleConfiguration initial, int stopAfterSyncs)
        {
            using var cancellation = new CancellationTokenSource();
            using var writer = new StringWriter();
            var syncs = 0;
            var exitCode = await ScheduledWorker.RunAsync(
                "job",
                initial,
                _ => reloads.Count == 0
                    ? Task.FromResult(new WorkerScheduleState(initial, true))
                    : reloads.Dequeue() switch
                    {
                        Exception exception => Task.FromException<WorkerScheduleState>(exception),
                        var state => Task.FromResult((WorkerScheduleState)state)
                    },
                _ =>
                {
                    Steps.Add("sync");
                    if (++syncs == stopAfterSyncs)
                    {
                        cancellation.Cancel();
                    }

                    return Task.FromResult(new WorkerSyncResult(0));
                },
                writer,
                true,
                cancellation.Token,
                (duration, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Steps.Add($"delay {duration:c}");
                    return Task.CompletedTask;
                });
            output = writer.ToString();
            return exitCode;
        }
    }
}
