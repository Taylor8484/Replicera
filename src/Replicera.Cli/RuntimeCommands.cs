using System.Diagnostics;
using System.Text.Json;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Replication;
using Replicera.Core.Schema;
using Replicera.Dataverse;
using Replicera.Dataverse.Authentication;
using Replicera.Dataverse.ChangeTracking;
using Replicera.Dataverse.Security;
using Replicera.Provider.Oracle;
using Replicera.Provider.PostgreSql;
using Replicera.Provider.SqlServer;

namespace Replicera.Cli;

internal static class RuntimeCommands
{
    public static async Task<int> BootstrapPermissionsAsync(
        string configPath,
        string? sourceName,
        string? roleName,
        TextWriter output,
        bool structuredOutput,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var source = sourceName is null
            ? configuration.Sources.Count == 1
                ? configuration.Sources[0]
                : throw new RepliceraException(ErrorCategory.Configuration, "Specify --name when configuration does not contain exactly one source.")
            : configuration.Sources.SingleOrDefault(item => string.Equals(item.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new RepliceraException(ErrorCategory.Configuration, $"Unknown source '{sourceName}'.");
        await using var service = DataverseClientFactory.Create(source, new EnvironmentSecretResolver());
        var result = await new DataversePermissionBootstrapper(service).ApplyAsync(
            roleName ?? DataversePermissionBootstrapper.DefaultRoleName,
            cancellationToken).ConfigureAwait(false);
        if (structuredOutput)
        {
            await WriteJsonAsync(output, result).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(
                $"Role '{result.RoleName}' {(result.RoleCreated ? "created" : "updated")} with {result.TableReadPrivileges} organization-level table Read privileges.").ConfigureAwait(false);
            await output.WriteLineAsync(result.RoleAssigned ? "Role assigned to the current application user." : "Role was already assigned to the current application user.").ConfigureAwait(false);
            await output.WriteLineAsync(result.SystemCustomizerAssigned ? "System Customizer assigned to the current application user." : "System Customizer was already assigned to the current application user.").ConfigureAwait(false);
            await output.WriteLineAsync("Confirm System Customizer is assigned, remove temporary System Administrator, then run inspect or sync.").ConfigureAwait(false);
        }

        return (int)ExitCode.Success;
    }

    public static async Task<int> InspectAsync(
        string configPath,
        string? jobName,
        string logicalName,
        TextWriter output,
        bool structuredOutput,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(configPath, jobName, cancellationToken).ConfigureAwait(false);
        await using var service = DataverseClientFactory.Create(context.Source, context.Secrets);
        var source = new DataverseSource(service);
        var tracking = new DataverseChangeTrackingManager(service);
        var table = await source.GetTableAsync(logicalName, cancellationToken).ConfigureAwait(false);
        var trackingStatus = await tracking.GetStatusAsync(logicalName, cancellationToken).ConfigureAwait(false);
        var schema = context.Provider.CreateSchemaManager(context.ConnectionString);
        var destination = await schema.ReadTableAsync(table, cancellationToken).ConfigureAwait(false);
        var plan = SchemaPlanner.Plan(table, destination, context.Job.Schema);
        var destinationSchema = destination?.Schema ?? context.Provider.DefaultSchema;

        var actions = new List<InspectionAction>();
        if (!trackingStatus.IsEnabled && trackingStatus.CanEnable && context.Job.Sync.EnableChangeTracking)
        {
            actions.Add(new InspectionAction("apply", $"Enable change tracking on '{logicalName}'."));
        }

        actions.AddRange(table.Columns
            .Where(column => !column.IsSupported)
            .Select(column => new InspectionAction(
                "report",
                $"Skip unsupported column '{column.LogicalName}': {column.UnsupportedReason}")));
        actions.AddRange(plan.Changes.Select(change => new InspectionAction(
            change.IsBlocking ? "block" : change.IsAutomatic ? "apply" : "report",
            change.Description)));
        if (structuredOutput)
        {
            await WriteJsonAsync(
                output,
                new
                {
                    source = table.LogicalName,
                    destination = $"{destinationSchema}.{table.DestinationName}",
                    primaryKey = table.PrimaryKey.LogicalName,
                    supportedColumns = table.Columns.Count(column => column.IsSupported),
                    unsupportedColumns = table.Columns.Count(column => !column.IsSupported),
                    changeTracking = FormatTracking(trackingStatus),
                    actions,
                    hasBlockingChanges = plan.HasBlockingChanges,
                    mutated = false
                }).ConfigureAwait(false);
            return plan.HasBlockingChanges ? (int)ExitCode.SchemaOrMetadata : (int)ExitCode.Success;
        }

        await output.WriteLineAsync($"Source: {table.LogicalName}").ConfigureAwait(false);
        await output.WriteLineAsync($"Destination: {destinationSchema}.{table.DestinationName}").ConfigureAwait(false);
        await output.WriteLineAsync($"Primary key: {table.PrimaryKey.LogicalName}").ConfigureAwait(false);
        await output.WriteLineAsync($"Columns: {table.Columns.Count(column => column.IsSupported)} supported, {table.Columns.Count(column => !column.IsSupported)} unsupported").ConfigureAwait(false);
        await output.WriteLineAsync($"Change tracking: {FormatTracking(trackingStatus)}").ConfigureAwait(false);
        await output.WriteLineAsync("Proposed actions:").ConfigureAwait(false);
        if (!trackingStatus.IsEnabled && trackingStatus.CanEnable && context.Job.Sync.EnableChangeTracking)
        {
            await output.WriteLineAsync($"  ENABLE change tracking on {logicalName}").ConfigureAwait(false);
        }

        foreach (var column in table.Columns.Where(column => !column.IsSupported))
        {
            await output.WriteLineAsync(
                $"  REPORT: Skip unsupported column '{column.LogicalName}': {column.UnsupportedReason}").ConfigureAwait(false);
        }

        foreach (var change in plan.Changes)
        {
            var marker = change.IsBlocking ? "BLOCK" : change.IsAutomatic ? "APPLY" : "REPORT";
            await output.WriteLineAsync($"  {marker}: {change.Description}").ConfigureAwait(false);
        }

        if (plan.Changes.Count == 0 && trackingStatus.IsEnabled && table.Columns.All(column => column.IsSupported))
        {
            await output.WriteLineAsync("  None").ConfigureAwait(false);
        }

        await output.WriteLineAsync("No changes have been made.").ConfigureAwait(false);
        return plan.HasBlockingChanges ? (int)ExitCode.SchemaOrMetadata : (int)ExitCode.Success;
    }

    public static async Task<int> SyncAsync(
        string configPath,
        string? jobName,
        string? tableFilter,
        bool forceInitial,
        TextWriter output,
        TextWriter diagnostics,
        bool structuredOutput,
        bool verbose,
        bool structuredDiagnostics,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(configPath, jobName, cancellationToken).ConfigureAwait(false);
        var tables = tableFilter is null
            ? context.Job.Tables
            : context.Job.Tables.Where(table => string.Equals(table, tableFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tables.Count == 0)
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                tableFilter is null ? "The selected job contains no tables." : $"Table '{tableFilter}' is not configured for job '{context.Job.Name}'.");
        }

        await using var service = DataverseClientFactory.Create(context.Source, context.Secrets);
        var source = new DataverseSource(service);
        var tracking = new DataverseChangeTrackingManager(service);
        var destinationConnection = context.Provider.CreateConnection(context.ConnectionString);
        await source.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        await destinationConnection.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        await context.Provider.EnsureMetadataStoreAsync(context.ConnectionString, cancellationToken).ConfigureAwait(false);

        foreach (var logicalName in tables)
        {
            var started = Stopwatch.GetTimestamp();
            var table = await source.GetTableAsync(logicalName, cancellationToken).ConfigureAwait(false);
            var trackingStatus = await tracking.GetStatusAsync(logicalName, cancellationToken).ConfigureAwait(false);
            if (!trackingStatus.IsEnabled)
            {
                if (!trackingStatus.CanEnable || !context.Job.Sync.EnableChangeTracking)
                {
                    throw new RepliceraException(
                        ErrorCategory.UnsupportedMetadata,
                        $"Change tracking is disabled for '{logicalName}' and cannot be enabled by the configured policy.");
                }

                await tracking.EnableAsync(logicalName, cancellationToken).ConfigureAwait(false);
            }

            var schema = context.Provider.CreateSchemaManager(context.ConnectionString);
            var currentDestination = await schema.ReadTableAsync(table, cancellationToken).ConfigureAwait(false);
            var plan = SchemaPlanner.Plan(table, currentDestination, context.Job.Schema);
            await schema.ApplySchemaPlanAsync(context.Job.Name, table, plan, cancellationToken).ConfigureAwait(false);

            var engine = new ReplicationEngine(
                new DataverseChangeReader(service),
                context.Provider.CreateWriter(context.ConnectionString),
                context.Provider.CreateStateStore(context.ConnectionString),
                verbose || structuredDiagnostics
                    ? new ReplicationConsoleLogger(diagnostics, structuredDiagnostics)
                    : null);
            var metrics = await engine.SyncAsync(
                context.Job.Name,
                table,
                context.Job.Sync.BatchSize,
                cancellationToken,
                forceInitial).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(started);
            if (structuredOutput)
            {
                await WriteJsonAsync(
                    output,
                    new
                    {
                        job = context.Job.Name,
                        table = logicalName,
                        status = "succeeded",
                        full = forceInitial,
                        durationMilliseconds = (long)duration.TotalMilliseconds,
                        pages = metrics.PagesProcessed,
                        received = metrics.RecordsReceived,
                        inserted = metrics.RecordsInserted,
                        updated = metrics.RecordsUpdated,
                        deleted = metrics.RecordsDeleted
                    }).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(
                    $"{logicalName}: succeeded in {duration.TotalSeconds:F1}s; {metrics.PagesProcessed} pages, {metrics.RecordsReceived} received, {metrics.RecordsInserted} inserted, {metrics.RecordsUpdated} updated, {metrics.RecordsDeleted} deleted").ConfigureAwait(false);
            }
        }

        return (int)ExitCode.Success;
    }

    public static async Task<int> StatusAsync(
        string configPath,
        string? jobName,
        TextWriter output,
        bool structuredOutput,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(configPath, jobName, cancellationToken).ConfigureAwait(false);
        var store = context.Provider.CreateStateStore(context.ConnectionString);
        var statuses = new List<StatusOutput>();
        foreach (var table in context.Job.Tables)
        {
            var state = await store.GetTableStateAsync(context.Job.Name, table, cancellationToken).ConfigureAwait(false);
            statuses.Add(new StatusOutput(
                context.Job.Name,
                table,
                state?.State.ToString() ?? "Uninitialized",
                state?.LastSuccessfulSyncUtc,
                state?.LastRunType,
                state?.LastRunStartedUtc,
                state?.LastRunCompletedUtc,
                state?.LastRunRecordsInserted,
                state?.LastRunRecordsUpdated,
                state?.LastRunRecordsDeleted,
                state?.LastErrorCode,
                state?.LastErrorMessage));
        }

        if (structuredOutput)
        {
            await WriteJsonAsync(output, statuses).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("TABLE\tSTATE\tLAST SUCCESS\tLAST RUN\tINSERT\tUPDATE\tDELETE\tERROR").ConfigureAwait(false);
            foreach (var status in statuses)
            {
                await output.WriteLineAsync(
                    $"{status.Table}\t{status.State}\t{status.LastSuccessfulSyncUtc?.ToString("u") ?? "-"}\t" +
                    $"{FormatLastRun(status)}\t{status.RecordsInserted?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}\t" +
                    $"{status.RecordsUpdated?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}\t{status.RecordsDeleted?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}\t" +
                    $"{FormatError(status)}").ConfigureAwait(false);
            }
        }

        return (int)ExitCode.Success;
    }

    private static async Task<RuntimeContext> LoadContextAsync(
        string configPath,
        string? jobName,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationFile.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var job = jobName is null
            ? configuration.Jobs.Count == 1
                ? configuration.Jobs[0]
                : throw new RepliceraException(ErrorCategory.Configuration, "Specify --job when configuration does not contain exactly one job.")
            : configuration.Jobs.SingleOrDefault(job => string.Equals(job.Name, jobName, StringComparison.OrdinalIgnoreCase))
                ?? throw new RepliceraException(ErrorCategory.Configuration, $"Unknown job '{jobName}'.");
        var source = configuration.Sources.Single(source => string.Equals(source.Name, job.Source, StringComparison.OrdinalIgnoreCase));
        var destination = configuration.Destinations.Single(destination => string.Equals(destination.Name, job.Destination, StringComparison.OrdinalIgnoreCase));
        var secrets = new EnvironmentSecretResolver();
        return new RuntimeContext(
            job,
            source,
            secrets.Resolve(destination.ConnectionStringEnvironmentVariable),
            CreateProvider(destination.Provider),
            secrets);
    }

    private static IDestinationProvider CreateProvider(string providerName) => providerName.ToLowerInvariant() switch
    {
        "sqlserver" => new SqlServerProvider(),
        "postgresql" or "postgres" => new PostgreSqlProvider(),
        "oracle" => new OracleProvider(),
        _ => throw new RepliceraException(ErrorCategory.Configuration, $"Unsupported provider '{providerName}'.")
    };

    private static string FormatTracking(Core.Abstractions.ChangeTrackingStatus status) =>
        status.IsEnabled ? "Enabled" : status.CanEnable ? "Disabled (can enable)" : $"Disabled ({status.BlockedReason})";

    private static string FormatLastRun(StatusOutput status) => status.LastRunStartedUtc is null
        ? "-"
        : $"{status.LastRunType ?? "Unknown"} {status.LastRunStartedUtc:u}";

    private static string FormatError(StatusOutput status) => status.ErrorCode is null
        ? "-"
        : $"{status.ErrorCode}: {status.ErrorMessage}";

    private static Task WriteJsonAsync<T>(TextWriter output, T value) =>
        output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record InspectionAction(string Disposition, string Description);

    private sealed record StatusOutput(
        string Job,
        string Table,
        string State,
        DateTimeOffset? LastSuccessfulSyncUtc,
        string? LastRunType,
        DateTimeOffset? LastRunStartedUtc,
        DateTimeOffset? LastRunCompletedUtc,
        long? RecordsInserted,
        long? RecordsUpdated,
        long? RecordsDeleted,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record RuntimeContext(
        JobConfiguration Job,
        SourceConfiguration Source,
        string ConnectionString,
        IDestinationProvider Provider,
        ISecretResolver Secrets);
}
