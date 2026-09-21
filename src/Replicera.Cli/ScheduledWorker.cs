using System.Text.Json;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;

namespace Replicera.Cli;

internal static class ScheduledWorker
{
    public static async Task<int> RunAsync(
        string jobName,
        ScheduleConfiguration schedule,
        Func<CancellationToken, Task<int>> synchronize,
        TextWriter output,
        bool structuredOutput,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(synchronize);
        ArgumentNullException.ThrowIfNull(output);

        if (!schedule.Enabled)
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                $"The schedule for job '{jobName}' is disabled.");
        }

        delay ??= static (duration, token) => Task.Delay(duration, token);
        timeProvider ??= TimeProvider.System;

        await WriteEventAsync(
            output,
            structuredOutput,
            timeProvider.GetUtcNow(),
            "workerStarted",
            jobName,
            schedule.Interval,
            null).ConfigureAwait(false);

        try
        {
            if (!schedule.RunOnStart)
            {
                await delay(schedule.Interval, cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteEventAsync(
                    output,
                    structuredOutput,
                    timeProvider.GetUtcNow(),
                    "syncStarted",
                    jobName,
                    schedule.Interval,
                    null).ConfigureAwait(false);

                var exitCode = await synchronize(cancellationToken).ConfigureAwait(false);
                await WriteEventAsync(
                    output,
                    structuredOutput,
                    timeProvider.GetUtcNow(),
                    exitCode == (int)ExitCode.Success ? "syncSucceeded" : "syncFailed",
                    jobName,
                    schedule.Interval,
                    exitCode).ConfigureAwait(false);

                await delay(schedule.Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteEventAsync(
                output,
                structuredOutput,
                timeProvider.GetUtcNow(),
                "workerStopped",
                jobName,
                schedule.Interval,
                null).ConfigureAwait(false);
            return (int)ExitCode.Success;
        }
    }

    private static Task WriteEventAsync(
        TextWriter output,
        bool structuredOutput,
        DateTimeOffset timestamp,
        string eventName,
        string jobName,
        TimeSpan interval,
        int? exitCode)
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
                    exitCode
                },
                JsonOptions));
        }

        var message = eventName switch
        {
            "workerStarted" => $"Worker started for job '{jobName}'; interval {interval:c}.",
            "syncStarted" => $"Starting scheduled synchronization for job '{jobName}'.",
            "syncSucceeded" => $"Scheduled synchronization for job '{jobName}' succeeded; next run in {interval:c}.",
            "syncFailed" => $"Scheduled synchronization for job '{jobName}' failed with exit code {exitCode}; next run in {interval:c}.",
            "workerStopped" => $"Worker stopped for job '{jobName}'.",
            _ => eventName
        };
        return output.WriteLineAsync($"{timestamp:u} {message}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
