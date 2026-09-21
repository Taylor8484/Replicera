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
    RecreateTable,
    DropTable,
    AddColumn,
    AddLookupTypeColumn,
    AddManagedColumn,
    RenameColumn,
    DropColumn,
    ExpandColumn,
    SourceColumnRemoved,
    IncompatibleColumn,
    OwnershipConflict
}

public static class ManagedColumnNames
{
    public const string DataLoadDate = "data_load_dte";

    public const string SourceRemoveDate = "date_source_remove_dte";
}
public sealed record SchemaChange(
    SchemaChangeKind Kind,
    string ObjectName,
    string Description,
    bool IsAutomatic,
    bool IsBlocking,
    string? NewObjectName = null);

public sealed record SchemaPlan(IReadOnlyList<SchemaChange> Changes)
{
    public bool HasBlockingChanges => Changes.Any(change => change.IsBlocking);
}
