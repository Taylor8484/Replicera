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

                    return Task.FromResult(0);
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
