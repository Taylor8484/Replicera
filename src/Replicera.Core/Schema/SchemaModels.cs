using Replicera.Core.Models;

namespace Replicera.Core.Schema;

public sealed record DestinationColumn(
    string Name,
    SourceType SourceType,
    bool IsNullable,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null);

public sealed record DestinationTable(
    string Schema,
    string Name,
    IReadOnlyList<DestinationColumn> Columns,
    bool IsManaged);

public enum SchemaChangeKind
{
    CreateTable,
    AddColumn,
    ExpandColumn,
    SourceColumnRemoved,
    IncompatibleColumn,
    OwnershipConflict
}
public sealed record SchemaChange(
    SchemaChangeKind Kind,
    string ObjectName,
    string Description,
    bool IsAutomatic,
    bool IsBlocking);

public sealed record SchemaPlan(IReadOnlyList<SchemaChange> Changes)
{
    public bool HasBlockingChanges => Changes.Any(change => change.IsBlocking);
}
