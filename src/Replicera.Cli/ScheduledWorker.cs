using System.Text.Json;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;

namespace Replicera.Cli;

internal sealed record WorkerScheduleState(ScheduleConfiguration? Schedule, bool JobExists);

internal static class ScheduledWorker
{
    /// <summary>How often a disabled schedule is checked for re-enablement, at most.</summary>
    internal static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(1);

    /// <summary>The longest wait after repeated failures, unless the interval itself is longer.</summary>
    internal static readonly TimeSpan MaximumFailureBackoff = TimeSpan.FromHours(1);

    public static Task<int> RunAsync(
        string jobName,
        ScheduleConfiguration schedule,
        Func<CancellationToken, Task<int>> synchronize,
        TextWriter output,
        bool structuredOutput,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        Func<double>? jitter = null) => RunAsync(
            jobName,
            schedule,
            _ => Task.FromResult(new WorkerScheduleState(schedule, true)),
            synchronize,
            output,
            structuredOutput,
            cancellationToken,
            delay,
            timeProvider,
            jitter);

    /// <summary>
    /// Runs the job on its schedule, reloading the schedule before every cycle. A disabled or
    /// removed schedule leaves the worker idle until it is enabled again, a configuration that fails
    /// validation keeps the last valid schedule, and a job removed from the configuration stops
    /// the worker.
    /// </summary>
    public static async Task<int> RunAsync(
        string jobName,
        ScheduleConfiguration schedule,
        Func<CancellationToken, Task<WorkerScheduleState>> reloadSchedule,
        Func<CancellationToken, Task<int>> synchronize,
        TextWriter output,
        bool structuredOutput,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        Func<double>? jitter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(reloadSchedule);
        ArgumentNullException.ThrowIfNull(synchronize);
        ArgumentNullException.ThrowIfNull(output);

        delay ??= static (duration, token) => Task.Delay(duration, token);
        timeProvider ??= TimeProvider.System;
        jitter ??= Random.Shared.NextDouble;
        var consecutiveFailures = 0;
        Task WriteAsync(string eventName, int? exitCode = null, string? message = null, TimeSpan? nextRunIn = null) => WriteEventAsync(
            output,
            structuredOutput,
            timeProvider.GetUtcNow(),
            eventName,
            jobName,
            schedule.Interval,
            exitCode,
            message,
            nextRunIn is null ? null : consecutiveFailures,
            nextRunIn);

        await WriteAsync("workerStarted").ConfigureAwait(false);
        try
        {
            var idle = !schedule.Enabled;
            if (idle)
            {
                await WriteAsync("scheduleDisabled").ConfigureAwait(false);
            }
            else if (!schedule.RunOnStart)
            {
                await delay(schedule.Interval, cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var state = await reloadSchedule(cancellationToken).ConfigureAwait(false);
                    if (!state.JobExists)
                    {
                        await WriteAsync("workerStopped", (int)ExitCode.InvalidInput, "The job no longer exists in the configuration.").ConfigureAwait(false);
                        return (int)ExitCode.InvalidInput;
                    }

                    if (state.Schedule is not { Enabled: true } enabled)
                    {
                        if (!idle)
                        {
                            idle = true;
                            await WriteAsync("scheduleDisabled").ConfigureAwait(false);
                        }

                        await delay(IdlePollInterval(schedule), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    schedule = enabled;
                    if (idle)
                    {
                        idle = false;
                        await WriteAsync("scheduleEnabled").ConfigureAwait(false);
                    }
                }
                catch (RepliceraException exception) when (exception.Category == ErrorCategory.Configuration)
                {
                    await WriteAsync("configurationInvalid", message: exception.Message).ConfigureAwait(false);
                    if (idle)
                    {
                        await delay(IdlePollInterval(schedule), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                await WriteAsync("syncStarted").ConfigureAwait(false);
                var exitCode = await synchronize(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = exitCode == (int)ExitCode.Success ? 0 : consecutiveFailures + 1;
                var nextRunIn = NextRunDelay(schedule.Interval, consecutiveFailures, jitter());
                await WriteAsync(exitCode == (int)ExitCode.Success ? "syncSucceeded" : "syncFailed", exitCode, nextRunIn: nextRunIn).ConfigureAwait(false);
                await delay(nextRunIn, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteAsync("workerStopped").ConfigureAwait(false);
            return (int)ExitCode.Success;
        }
    }

    // Repeated failures wait twice as long each time, starting from the interval, so a permanent
    // problem such as rejected credentials does not generate a request every interval. Jitter of
    // plus or minus 20 percent keeps workers that failed together from retrying together, and the
    // wait never exceeds the larger of the interval and the backoff ceiling.
    internal static TimeSpan NextRunDelay(TimeSpan interval, int consecutiveFailures, double jitter)
    {
        if (consecutiveFailures == 0)
        {
            return interval;
        }

        var ceiling = interval > MaximumFailureBackoff ? interval : MaximumFailureBackoff;
        var backoff = interval.Ticks * Math.Pow(2, Math.Min(consecutiveFailures - 1, 30)) * (0.8 + (0.4 * jitter));
        return backoff < ceiling.Ticks ? TimeSpan.FromTicks((long)backoff) : ceiling;
    }

    private static TimeSpan IdlePollInterval(ScheduleConfiguration schedule) =>
        schedule.Interval < DisabledPollInterval ? schedule.Interval : DisabledPollInterval;

    private static Task WriteEventAsync(
        TextWriter output,
        bool structuredOutput,
        DateTimeOffset timestamp,
        string eventName,
        string jobName,
        TimeSpan interval,
        int? exitCode,
        string? message,
        int? consecutiveFailures,
        TimeSpan? nextRunIn)
    {
        if (structuredOutput)
        {
            return output.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    timestamp,
                    eventName,
                    job = jobName,
                    interval,
                    exitCode,
                    message,
                    consecutiveFailures,
                    nextRunIn
                },
                JsonOptions));
        }

        var text = eventName switch
        {
            "workerStarted" => $"Worker started for job '{jobName}'; interval {interval:c}.",
            "syncStarted" => $"Starting scheduled synchronization for job '{jobName}'.",
            "syncSucceeded" => $"Scheduled synchronization for job '{jobName}' succeeded; next run in {nextRunIn ?? interval:c}.",
            "syncFailed" => $"Scheduled synchronization for job '{jobName}' failed with exit code {exitCode} ({consecutiveFailures} consecutive); next run in {nextRunIn ?? interval:c}.",
            "workerStopped" when message is not null => $"Worker stopped for job '{jobName}': {message}",
            "workerStopped" => $"Worker stopped for job '{jobName}'.",
            "scheduleDisabled" => $"Schedule for job '{jobName}' is disabled; the worker is idle until it is enabled.",
            "scheduleEnabled" => $"Schedule for job '{jobName}' is enabled; resuming synchronization.",
            "configurationInvalid" => $"Configuration for job '{jobName}' could not be reloaded; continuing with the last valid schedule: {message}",
            _ => eventName
        };
        return output.WriteLineAsync($"{timestamp:u} {text}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
