namespace Replicera.Core.Models;

public sealed record ColumnDefinition
{
    public required string LogicalName { get; init; }

    public required SourceType SourceType { get; init; }

    public bool IsNullable { get; init; }

    public bool IsPrimaryKey { get; init; }

    public int? MaxLength { get; init; }

    public int? Precision { get; init; }

    public int? Scale { get; init; }

    public DateTimeBehavior? DateTimeBehavior { get; init; }

    public IReadOnlyList<string> LookupTargets { get; init; } = [];

    public bool IsCalculated { get; init; }

    public bool IsRollup { get; init; }

    public string? UnsupportedReason { get; init; }

    public bool IsSupported => UnsupportedReason is null;
}
