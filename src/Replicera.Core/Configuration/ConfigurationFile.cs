using System.Text.Json;
using System.Text.Json.Serialization;
using Replicera.Core.Errors;

namespace Replicera.Core.Configuration;

public static class ConfigurationFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<RepliceraConfiguration> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            await using var stream = File.OpenRead(path);
            var configuration = await JsonSerializer.DeserializeAsync<RepliceraConfiguration>(
                stream,
                Options,
                cancellationToken).ConfigureAwait(false)
                ?? throw new RepliceraException(ErrorCategory.Configuration, "Configuration file is empty.");
            var issues = ConfigurationValidator.Validate(configuration);
            if (issues.Count > 0)
            {
                var message = string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}"));
                throw new RepliceraException(ErrorCategory.Configuration, message);
            }

            return configuration;
        }
        catch (RepliceraException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                $"Could not load configuration '{path}': {exception.Message}",
                exception);
        }
    }

    public static async Task CreateAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(
                stream,
                new RepliceraConfiguration(),
                Options,
                cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception) when (File.Exists(path))
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                $"Configuration file '{path}' already exists.",
                exception);
        }
    }

    public static async Task SaveAsync(
        string path,
        RepliceraConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = ConfigurationValidator.Validate(configuration);
        if (issues.Count > 0)
        {
            var message = string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}"));
            throw new RepliceraException(ErrorCategory.Configuration, message);
        }

        var fullPath = Path.GetFullPath(path);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, Options, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                $"Could not save configuration '{path}': {exception.Message}",
                exception);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
