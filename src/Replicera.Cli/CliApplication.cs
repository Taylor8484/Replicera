using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.PowerPlatform.Dataverse.Client.Utils;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;

namespace Replicera.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) => await RunAsync(
            arguments,
            Console.In,
            output,
            error,
            cancellationToken).ConfigureAwait(false);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            if (arguments.Count == 1 && arguments[0] is "--version" or "-V")
            {
                await output.WriteLineAsync(ProductVersion).ConfigureAwait(false);
                return (int)ExitCode.Success;
            }

            if (arguments.Count == 0 || arguments[0] is "--help" or "-h" or "help")
            {
                await output.WriteLineAsync(HelpText).ConfigureAwait(false);
                return (int)ExitCode.Success;
            }

            var configPath = GetOption(arguments, "--config") ?? "replicera.json";
            var structuredOutput = HasFlag(arguments, "--json");
            return arguments[0] switch
            {
                "init" => await InitializeAsync(configPath, output, cancellationToken).ConfigureAwait(false),
                "source" when HasSubcommand(arguments, "add") => await AddSourceAsync(arguments, configPath, input, output, cancellationToken).ConfigureAwait(false),
                "source" when HasSubcommand(arguments, "list") => await ListSourcesAsync(configPath, output, cancellationToken).ConfigureAwait(false),
                "source" when HasSubcommand(arguments, "bootstrap-permissions") => await RuntimeCommands.BootstrapPermissionsAsync(
                    configPath,
                    GetOption(arguments, "--name"),
                    GetOption(arguments, "--role-name"),
                    output,
                    structuredOutput,
                    cancellationToken).ConfigureAwait(false),
                "destination" when HasSubcommand(arguments, "add") => await AddDestinationAsync(arguments, configPath, input, output, cancellationToken).ConfigureAwait(false),
                "destination" when HasSubcommand(arguments, "list") => await ListDestinationsAsync(configPath, output, cancellationToken).ConfigureAwait(false),
                "job" when HasSubcommand(arguments, "add") => await AddJobAsync(arguments, configPath, input, output, cancellationToken).ConfigureAwait(false),
                "job" when HasSubcommand(arguments, "list") => await ListJobsAsync(configPath, output, cancellationToken).ConfigureAwait(false),
                "tables" when HasSubcommand(arguments, "add") && arguments.Count > 2 => await AddTableAsync(arguments, configPath, output, cancellationToken).ConfigureAwait(false),
                "tables" when HasSubcommand(arguments, "remove") && arguments.Count > 2 => await RemoveTableAsync(arguments, configPath, output, cancellationToken).ConfigureAwait(false),
                "tables" when HasSubcommand(arguments, "list") => await ListConfiguredTablesAsync(configPath, output, cancellationToken).ConfigureAwait(false),
                "inspect" when arguments.Count > 1 => await RuntimeCommands.InspectAsync(
                    configPath,
                    GetOption(arguments, "--job"),
                    arguments[1],
                    output,
                    structuredOutput,
                    cancellationToken).ConfigureAwait(false),
                "sync" => await RuntimeCommands.SyncAsync(
                    configPath,
                    GetOption(arguments, "--job"),
                    GetOption(arguments, "--table"),
                    HasFlag(arguments, "--full"),
                    output,
                    error,
                    structuredOutput,
                    HasFlag(arguments, "--verbose"),
                    HasFlag(arguments, "--log-json"),
                    cancellationToken).ConfigureAwait(false),
                "status" => await RuntimeCommands.StatusAsync(
                    configPath,
                    GetOption(arguments, "--job"),
                    output,
                    structuredOutput,
                    cancellationToken).ConfigureAwait(false),
                _ => await InvalidCommandAsync(arguments, error).ConfigureAwait(false)
            };
        }
        catch (RepliceraException exception)
        {
            await error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCodeMapper.From(exception.Category);
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("error: operation cancelled").ConfigureAwait(false);
            return (int)ExitCode.SynchronizationFailure;
        }
        catch (DataverseConnectionException)
        {
            await error.WriteLineAsync("error: Dataverse authentication or connection failed.").ConfigureAwait(false);
            return (int)ExitCode.AuthenticationOrAuthorization;
        }
        catch (DataverseOperationException)
        {
            await error.WriteLineAsync("error: Dataverse operation failed.").ConfigureAwait(false);
            return (int)ExitCode.SourceConnectivity;
        }
        catch (SqlException exception)
        {
            await error.WriteLineAsync($"error: SQL Server operation failed (error {exception.Number}).").ConfigureAwait(false);
            return (int)ExitCode.DestinationConnectivity;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("error: unexpected internal failure.").ConfigureAwait(false);
            return (int)ExitCode.Unexpected;
        }
    }

    private static async Task<int> InitializeAsync(string path, TextWriter output, CancellationToken cancellationToken)
    {
        await ConfigurationFile.CreateAsync(path, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Created {path}").ConfigureAwait(false);
        return (int)ExitCode.Success;
    }

    private static async Task<int> ListSourcesAsync(string path, TextWriter output, CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var source in configuration.Sources.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"{source.Name}\t{source.Url}").ConfigureAwait(false);
        }

        return (int)ExitCode.Success;
    }

    private static async Task<int> AddSourceAsync(
        IReadOnlyList<string> arguments,
        string path,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var interactive = HasFlag(arguments, "--interactive");
        var name = await GetRequiredValueAsync(arguments, "--name", "Source name", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var urlText = await GetRequiredValueAsync(arguments, "--url", "Dataverse URL", interactive, input, output, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
        {
            throw new RepliceraException(ErrorCategory.Configuration, $"Option '--url' is not a valid absolute URL: '{urlText}'.");
        }

        var authenticationText = GetOption(arguments, "--auth")
            ?? (interactive
                ? await PromptAsync(input, output, "Authentication [client-secret|certificate]", "client-secret", cancellationToken).ConfigureAwait(false)
                : "client-secret");
        var authentication = ParseAuthentication(authenticationText);
        var tenantId = await GetRequiredValueAsync(arguments, "--tenant-id", "Tenant ID", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var clientId = await GetRequiredValueAsync(arguments, "--client-id", "Client ID", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var secretEnvironment = await GetRequiredValueAsync(arguments, "--secret-env", "Secret environment variable", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var certificatePasswordEnvironment = GetOption(arguments, "--certificate-password-env");
        if (interactive && authentication == AuthenticationMethod.Certificate && certificatePasswordEnvironment is null)
        {
            certificatePasswordEnvironment = await PromptAsync(
                input,
                output,
                "Certificate password environment variable (optional)",
                null,
                cancellationToken).ConfigureAwait(false);
            certificatePasswordEnvironment = string.IsNullOrWhiteSpace(certificatePasswordEnvironment)
                ? null
                : certificatePasswordEnvironment;
        }

        var source = new SourceConfiguration
        {
            Name = name,
            Url = url,
            TenantId = ParseGuid(tenantId, "--tenant-id"),
            ClientId = ParseGuid(clientId, "--client-id"),
            Authentication = new AuthenticationConfiguration
            {
                Method = authentication,
                SecretEnvironmentVariable = secretEnvironment,
                CertificatePasswordEnvironmentVariable = certificatePasswordEnvironment
            }
        };
        var updated = configuration with { Sources = [.. configuration.Sources, source] };
        await ConfigurationFile.SaveAsync(path, updated, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Added source '{name}'.").ConfigureAwait(false);
        return (int)ExitCode.Success;
    }

    private static async Task<int> ListDestinationsAsync(string path, TextWriter output, CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var destination in configuration.Destinations.OrderBy(destination => destination.Name, StringComparer.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"{destination.Name}\t{destination.Provider}").ConfigureAwait(false);
        }

        return (int)ExitCode.Success;
    }

    private static async Task<int> AddDestinationAsync(
        IReadOnlyList<string> arguments,
        string path,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var interactive = HasFlag(arguments, "--interactive");
        var name = await GetRequiredValueAsync(arguments, "--name", "Destination name", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var provider = GetOption(arguments, "--provider")
            ?? (interactive
                ? await PromptAsync(input, output, "Provider", "sqlserver", cancellationToken).ConfigureAwait(false)
                : "sqlserver");
        var connectionEnvironment = await GetRequiredValueAsync(
            arguments,
            "--connection-env",
            "Connection-string environment variable",
            interactive,
            input,
            output,
            cancellationToken).ConfigureAwait(false);
        var destination = new DestinationConfiguration
        {
            Name = name,
            Provider = provider,
            ConnectionStringEnvironmentVariable = connectionEnvironment
        };
        var updated = configuration with { Destinations = [.. configuration.Destinations, destination] };
        await ConfigurationFile.SaveAsync(path, updated, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Added destination '{destination.Name}'.").ConfigureAwait(false);
        return (int)ExitCode.Success;
    }

    private static async Task<int> ListConfiguredTablesAsync(string path, TextWriter output, CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var job in configuration.Jobs.OrderBy(job => job.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var table in job.Tables.Order(StringComparer.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync($"{job.Name}\t{table}").ConfigureAwait(false);
            }
        }

        return (int)ExitCode.Success;
    }

    private static async Task<int> AddJobAsync(
        IReadOnlyList<string> arguments,
        string path,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var interactive = HasFlag(arguments, "--interactive");
        var name = await GetRequiredValueAsync(arguments, "--name", "Job name", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var source = await GetRequiredValueAsync(arguments, "--source", "Source name", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var destination = await GetRequiredValueAsync(arguments, "--destination", "Destination name", interactive, input, output, cancellationToken).ConfigureAwait(false);
        var tables = GetOptions(arguments, "--table");
        if (tables.Count == 0 && interactive)
        {
            tables.Add(await PromptAsync(input, output, "Initial table logical name", null, cancellationToken).ConfigureAwait(false));
        }

        if (tables.Count == 0 || tables.Any(string.IsNullOrWhiteSpace))
        {
            throw new RepliceraException(ErrorCategory.Configuration, "At least one '--table' value is required.");
        }

        var batchSizeText = GetOption(arguments, "--batch-size")
            ?? (interactive
                ? await PromptAsync(input, output, "Batch size", "5000", cancellationToken).ConfigureAwait(false)
                : "5000");
        if (!int.TryParse(batchSizeText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var batchSize))
        {
            throw new RepliceraException(ErrorCategory.Configuration, $"Option '--batch-size' is not a valid integer: '{batchSizeText}'.");
        }

        var modeText = GetOption(arguments, "--mode")
            ?? (interactive
                ? await PromptAsync(input, output, "Synchronization mode (complete, no-data-loss, reload)", "complete", cancellationToken).ConfigureAwait(false)
                : "complete");
        var mode = modeText.ToLowerInvariant() switch
        {
            "complete" => SynchronizationMode.Complete,
            "no-data-loss" or "nodataloss" => SynchronizationMode.NoDataLoss,
            "reload" => SynchronizationMode.Reload,
            _ => throw new RepliceraException(
                ErrorCategory.Configuration,
                $"Option '--mode' must be complete, no-data-loss, or reload: '{modeText}'.")
        };

        var job = new JobConfiguration
        {
            Name = name,
            Source = source,
            Destination = destination,
            Tables = tables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Sync = new SyncPolicy { BatchSize = batchSize, Mode = mode }
        };
        var updated = configuration with { Jobs = [.. configuration.Jobs, job] };
        await ConfigurationFile.SaveAsync(path, updated, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Added job '{name}'.").ConfigureAwait(false);
        return (int)ExitCode.Success;
    }

    private static async Task<int> ListJobsAsync(string path, TextWriter output, CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var job in configuration.Jobs.OrderBy(job => job.Name, StringComparer.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"{job.Name}\t{job.Source}\t{job.Destination}\t{job.Tables.Count} tables\t{FormatMode(job.Sync.Mode)}").ConfigureAwait(false);
        }

        return (int)ExitCode.Success;
    }

    private static Task<int> AddTableAsync(
        IReadOnlyList<string> arguments,
        string path,
        TextWriter output,
        CancellationToken cancellationToken) =>
        ChangeTableAsync(arguments, path, output, true, cancellationToken);

    private static Task<int> RemoveTableAsync(
        IReadOnlyList<string> arguments,
        string path,
        TextWriter output,
        CancellationToken cancellationToken) =>
        ChangeTableAsync(arguments, path, output, false, cancellationToken);

    private static async Task<int> ChangeTableAsync(
        IReadOnlyList<string> arguments,
        string path,
        TextWriter output,
        bool add,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var selectedJob = SelectJob(configuration, GetOption(arguments, "--job"));
        var table = arguments[2];
        if (string.IsNullOrWhiteSpace(table) || table.StartsWith("--", StringComparison.Ordinal))
        {
            throw new RepliceraException(ErrorCategory.Configuration, "A table logical name is required.");
        }

        var contains = selectedJob.Tables.Contains(table, StringComparer.OrdinalIgnoreCase);
        if (add && contains)
        {
            throw new RepliceraException(ErrorCategory.Configuration, $"Table '{table}' is already configured for job '{selectedJob.Name}'.");
        }

        if (!add && !contains)
        {
            throw new RepliceraException(ErrorCategory.Configuration, $"Table '{table}' is not configured for job '{selectedJob.Name}'.");
        }

        var tables = add
            ? selectedJob.Tables.Append(table).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : selectedJob.Tables.Where(existing => !string.Equals(existing, table, StringComparison.OrdinalIgnoreCase)).ToArray();
        var changedJob = selectedJob with { Tables = tables };
        var updated = configuration with
        {
            Jobs = configuration.Jobs.Select(job => ReferenceEquals(job, selectedJob) ? changedJob : job).ToArray()
        };
        await ConfigurationFile.SaveAsync(path, updated, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"{(add ? "Added" : "Removed")} table '{table}' {(add ? "to" : "from")} job '{selectedJob.Name}'.").ConfigureAwait(false);
        return (int)ExitCode.Success;
    }

    private static async Task<int> InvalidCommandAsync(IReadOnlyList<string> arguments, TextWriter error)
    {
        await error.WriteLineAsync($"error: unknown command '{string.Join(' ', arguments)}'").ConfigureAwait(false);
        await error.WriteLineAsync("Run 'replicera --help' for usage.").ConfigureAwait(false);
        return (int)ExitCode.InvalidInput;
    }

    private static bool HasSubcommand(IReadOnlyList<string> arguments, string subcommand) =>
        arguments.Count > 1 && string.Equals(arguments[1], subcommand, StringComparison.Ordinal);

    private static bool HasFlag(IReadOnlyList<string> arguments, string flag) =>
        arguments.Contains(flag, StringComparer.Ordinal);

    private static string? GetOption(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new RepliceraException(ErrorCategory.Configuration, $"Option '{option}' requires a value.");
            }

            return arguments[index + 1];
        }

        return null;
    }

    private static List<string> GetOptions(IReadOnlyList<string> arguments, string option)
    {
        var values = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new RepliceraException(ErrorCategory.Configuration, $"Option '{option}' requires a value.");
            }

            values.Add(arguments[index + 1]);
        }

        return values;
    }

    private static Guid ParseGuid(string value, string option)
    {
        return Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new RepliceraException(ErrorCategory.Configuration, $"Option '{option}' is not a valid GUID.");
    }

    private static AuthenticationMethod ParseAuthentication(string value) => value.ToLowerInvariant() switch
    {
        "client-secret" or "clientsecret" => AuthenticationMethod.ClientSecret,
        "certificate" => AuthenticationMethod.Certificate,
        _ => throw new RepliceraException(ErrorCategory.Configuration, $"Unknown authentication method '{value}'.")
    };

    private static string FormatMode(SynchronizationMode mode) => mode switch
    {
        SynchronizationMode.Complete => "complete",
        SynchronizationMode.NoDataLoss => "noDataLoss",
        SynchronizationMode.Reload => "reload",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown synchronization mode.")
    };

    private static async Task<string> GetRequiredValueAsync(
        IReadOnlyList<string> arguments,
        string option,
        string prompt,
        bool interactive,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var value = GetOption(arguments, option);
        if (value is not null)
        {
            return value;
        }

        if (!interactive)
        {
            throw new RepliceraException(ErrorCategory.Configuration, $"Option '{option}' is required.");
        }

        value = await PromptAsync(input, output, prompt, null, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value)
            ? throw new RepliceraException(ErrorCategory.Configuration, $"A value is required for '{option}'.")
            : value;
    }

    private static async Task<string> PromptAsync(
        TextReader input,
        TextWriter output,
        string prompt,
        string? defaultValue,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(defaultValue is null ? $"{prompt}: " : $"{prompt} [{defaultValue}]: ").ConfigureAwait(false);
        var value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            if (defaultValue is not null)
            {
                return defaultValue;
            }

            throw new RepliceraException(ErrorCategory.Configuration, $"Input ended while reading '{prompt}'.");
        }

        return string.IsNullOrWhiteSpace(value) && defaultValue is not null ? defaultValue : value.Trim();
    }

    private static JobConfiguration SelectJob(RepliceraConfiguration configuration, string? jobName)
    {
        if (jobName is null)
        {
            return configuration.Jobs.Count == 1
                ? configuration.Jobs[0]
                : throw new RepliceraException(ErrorCategory.Configuration, "Specify --job when configuration does not contain exactly one job.");
        }

        return configuration.Jobs.SingleOrDefault(job => string.Equals(job.Name, jobName, StringComparison.OrdinalIgnoreCase))
            ?? throw new RepliceraException(ErrorCategory.Configuration, $"Unknown job '{jobName}'.");
    }

    private const string HelpText = """
        Replicera - mirror Microsoft Dataverse tables to relational databases

        Usage:
          replicera init [--config <path>]
          replicera source add [--interactive] --name <name> --url <url> --tenant-id <guid> --client-id <guid> --secret-env <variable> [--auth client-secret|certificate] [--certificate-password-env <variable>] [--config <path>]
          replicera source list [--config <path>]
          replicera source bootstrap-permissions [--name <source>] [--role-name <role>] [--json] [--config <path>]
          replicera destination add [--interactive] --name <name> --connection-env <variable> [--provider sqlserver|postgresql|oracle] [--config <path>]
          replicera destination list [--config <path>]
          replicera job add [--interactive] --name <name> --source <source> --destination <destination> --table <table> [--table <table>] [--batch-size <count>] [--mode complete|no-data-loss|reload] [--config <path>]
          replicera job list [--config <path>]
          replicera tables list [--config <path>]
          replicera tables add <table> [--job <name>] [--config <path>]
          replicera tables remove <table> [--job <name>] [--config <path>]
          replicera inspect <table> [--job <name>] [--json] [--config <path>]
          replicera sync [--job <name>] [--table <table>] [--full] [--json] [--verbose] [--log-json] [--config <path>]
          replicera status [--job <name>] [--json] [--config <path>]
        """;

    private static string ProductVersion =>
        typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0] ?? "unknown";
}
