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
        RespectNullableAnnotations = true,
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                $"Could not create configuration '{path}': {exception.Message}",
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

        var targetPath = ResolveSaveTarget(Path.GetFullPath(path));
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            UnixFileMode? existingMode = null;
            if (!OperatingSystem.IsWindows() && File.Exists(targetPath))
            {
                existingMode = File.GetUnixFileMode(targetPath);
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, fileOptions))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, Options, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows() && existingMode is { } mode)
            {
                File.SetUnixFileMode(temporaryPath, mode);
            }

            File.Move(temporaryPath, targetPath, true);
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

    // Replace the file a symbolic link points to rather than the link itself, so a linked
    // configuration keeps working after an update.
    private static string ResolveSaveTarget(string fullPath)
    {
        try
        {
            return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (IOException)
        {
            return fullPath;
        }
    }
}
