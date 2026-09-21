using System.Text.Json;
using Microsoft.Extensions.Logging;
using Replicera.Core.Replication;

namespace Replicera.Cli;

internal sealed class ReplicationConsoleLogger(
    TextWriter writer,
    bool structured,
    TimeProvider? timeProvider = null) : ILogger<ReplicationEngine>
{
    private readonly object writeLock = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = structured
            ? FormatJson(logLevel, eventId, state)
            : $"{logLevel.ToString().ToLowerInvariant()}: {formatter(state, null)}";
        lock (writeLock)
        {
            writer.WriteLine(line);
        }
    }

    private string FormatJson<TState>(LogLevel level, EventId eventId, TState state)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values
                .Where(value => value.Key != "{OriginalFormat}")
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>();
        return JsonSerializer.Serialize(
            new
            {
                timestamp = clock.GetUtcNow(),
                level = level.ToString(),
                eventId = eventId.Id,
                eventName = eventId.Name,
                properties
            },
            JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
