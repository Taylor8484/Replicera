namespace Replicera.Core.Configuration;

public sealed record ValidationIssue(string Path, string Message);

public static class ConfigurationValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(RepliceraConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ValidationIssue>();
        ValidateUniqueNames(configuration.Sources.Select(source => source.Name), "sources", issues);
        ValidateUniqueNames(configuration.Destinations.Select(destination => destination.Name), "destinations", issues);
        ValidateUniqueNames(configuration.Jobs.Select(job => job.Name), "jobs", issues);

        var sourceNames = configuration.Sources.Select(source => source.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinationNames = configuration.Destinations.Select(destination => destination.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < configuration.Sources.Count; index++)
        {
            var source = configuration.Sources[index];
            var path = $"sources[{index}]";
            ValidateName(source.Name, $"{path}.name", issues);
            if (!source.Url.IsAbsoluteUri || source.Url.Scheme != Uri.UriSchemeHttps)
            {
                issues.Add(new($"{path}.url", "Dataverse URL must be an absolute HTTPS URI."));
            }

            ValidateEnvironmentVariable(source.Authentication.SecretEnvironmentVariable, $"{path}.authentication.secretEnvironmentVariable", issues);
        }

        for (var index = 0; index < configuration.Destinations.Count; index++)
        {
            var destination = configuration.Destinations[index];
            var path = $"destinations[{index}]";
            ValidateName(destination.Name, $"{path}.name", issues);
            if (!SupportedProviders.Contains(destination.Provider))
            {
                issues.Add(new($"{path}.provider", $"Unsupported provider '{destination.Provider}'."));
            }

            ValidateEnvironmentVariable(destination.ConnectionStringEnvironmentVariable, $"{path}.connectionStringEnvironmentVariable", issues);
        }

        for (var index = 0; index < configuration.Jobs.Count; index++)
        {
            var job = configuration.Jobs[index];
            var path = $"jobs[{index}]";
            ValidateName(job.Name, $"{path}.name", issues);
            if (!sourceNames.Contains(job.Source))
            {
                issues.Add(new($"{path}.source", $"Unknown source '{job.Source}'."));
            }

            if (!destinationNames.Contains(job.Destination))
            {
                issues.Add(new($"{path}.destination", $"Unknown destination '{job.Destination}'."));
            }

            if (job.Tables.Count == 0)
            {
                issues.Add(new($"{path}.tables", "At least one table is required."));
            }
            else if (job.Tables.Distinct(StringComparer.OrdinalIgnoreCase).Count() != job.Tables.Count)
            {
                issues.Add(new($"{path}.tables", "Table names must be unique within a job."));
            }

            if (job.Sync.BatchSize is < 1 or > 5_000)
            {
                issues.Add(new($"{path}.sync.batchSize", "Batch size must be between 1 and 5000."));
            }

            if (job.Schedule is not null &&
                (job.Schedule.Interval < TimeSpan.FromSeconds(1) || job.Schedule.Interval > TimeSpan.FromDays(7)))
            {
                issues.Add(new(
                    $"{path}.schedule.interval",
                    "Schedule interval must be between one second and seven days."));
            }

            ValidateColumnRenames(job, path, issues);
        }

        return issues;
    }

    private static void ValidateColumnRenames(
        JobConfiguration job,
        string jobPath,
        List<ValidationIssue> issues)
    {
        foreach (var table in job.Schema.ColumnRenames)
        {
            var path = $"{jobPath}.schema.columnRenames.{table.Key}";
            if (!job.Tables.Contains(table.Key, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new(path, $"Rename mappings reference table '{table.Key}', which is not configured for the job."));
            }

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rename in table.Value)
            {
                if (string.IsNullOrWhiteSpace(rename.Key) || string.IsNullOrWhiteSpace(rename.Value))
                {
                    issues.Add(new(path, "Column rename source and target names are required."));
                }
                else if (string.Equals(rename.Key, rename.Value, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new(path, $"Column rename '{rename.Key}' must change the column name."));
                }
                else if (!targets.Add(rename.Value))
                {
                    issues.Add(new(path, $"Multiple columns cannot be renamed to '{rename.Value}'."));
                }
            }
        }
    }

    private static readonly HashSet<string> SupportedProviders =
        new(["sqlserver", "postgresql", "postgres", "oracle"], StringComparer.OrdinalIgnoreCase);

    private static void ValidateUniqueNames(IEnumerable<string> names, string path, List<ValidationIssue> issues)
    {
        foreach (var duplicate in names
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(new(path, $"Name '{duplicate.Key}' is duplicated."));
        }
    }

    private static void ValidateName(string name, string path, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new(path, "Name is required."));
        }
    }

    private static void ValidateEnvironmentVariable(string name, string path, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new(path, "Environment variable name is required."));
        }
    }
}
