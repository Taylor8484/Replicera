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

    /// <summary>
    /// The most digits a value can have before the decimal point when the source enforces a
    /// narrower range than <see cref="Precision"/> and <see cref="Scale"/> imply. Schema planning
    /// uses it to decide whether a scale change can be applied without losing existing values.
    /// </summary>
    public int? MaxIntegerDigits { get; init; }

    public DateTimeBehavior? DateTimeBehavior { get; init; }

    public IReadOnlyList<string> LookupTargets { get; init; } = [];

    public bool IsCalculated { get; init; }

    public bool IsRollup { get; init; }

    public string? UnsupportedReason { get; init; }

    public bool IsSupported => UnsupportedReason is null;
}
