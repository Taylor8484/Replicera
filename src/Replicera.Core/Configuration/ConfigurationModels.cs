namespace Replicera.Core.Configuration;

public sealed record RepliceraConfiguration
{
    public IReadOnlyList<SourceConfiguration> Sources { get; init; } = [];

    public IReadOnlyList<DestinationConfiguration> Destinations { get; init; } = [];

    public IReadOnlyList<JobConfiguration> Jobs { get; init; } = [];
}

public sealed record SourceConfiguration
{
    public required string Name { get; init; }

    public required Uri Url { get; init; }

    public required Guid TenantId { get; init; }

    public required Guid ClientId { get; init; }

    public required AuthenticationConfiguration Authentication { get; init; }
}

public enum AuthenticationMethod
{
    ClientSecret,
    Certificate
}

public sealed record AuthenticationConfiguration
{
    public required AuthenticationMethod Method { get; init; }

    public required string SecretEnvironmentVariable { get; init; }

    public string? CertificatePasswordEnvironmentVariable { get; init; }
}

public sealed record DestinationConfiguration
{
    public required string Name { get; init; }

    public required string Provider { get; init; }

    public required string ConnectionStringEnvironmentVariable { get; init; }
}

public sealed record JobConfiguration
{
    public required string Name { get; init; }

    public required string Source { get; init; }

    public required string Destination { get; init; }

    public IReadOnlyList<string> Tables { get; init; } = [];

    public SyncPolicy Sync { get; init; } = new();

    public SchemaPolicy Schema { get; init; } = new();
}

public sealed record SyncPolicy
{
    public SynchronizationMode Mode { get; init; } = SynchronizationMode.Complete;

    public bool EnableChangeTracking { get; init; } = true;

    public int BatchSize { get; init; } = 5_000;
}

public enum SynchronizationMode
{
    Complete,
    NoDataLoss,
    Reload
}

public enum SchemaAction
{
    Automatic,
    Manual,
    Stop
}

public sealed record SchemaPolicy
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ColumnRenames { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    public SchemaAction CreateTables { get; init; } = SchemaAction.Automatic;

    public SchemaAction AddColumns { get; init; } = SchemaAction.Automatic;

    public SchemaAction ExpandCompatibleColumns { get; init; } = SchemaAction.Automatic;

    public SchemaAction DropColumns { get; init; } = SchemaAction.Automatic;

    public SchemaAction DropTables { get; init; } = SchemaAction.Manual;

    public SchemaAction IncompatibleChanges { get; init; } = SchemaAction.Stop;
}
