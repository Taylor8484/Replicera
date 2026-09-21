using Replicera.Core.Configuration;
using Replicera.Core.Models;

namespace Replicera.Core.Schema;

public static class SchemaPlanner
{
    public static SchemaPlan Plan(
        TableDefinition source,
        DestinationTable? destination,
        SchemaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);

        if (destination is null)
        {
            return new SchemaPlan(
            [
                new SchemaChange(
                    SchemaChangeKind.CreateTable,
                    source.DestinationName,
                    $"Create destination table for '{source.LogicalName}'.",
                    policy.CreateTables == SchemaAction.Automatic,
                    policy.CreateTables != SchemaAction.Automatic)
            ]);
        }

        if (!destination.IsManaged)
        {
            return new SchemaPlan(
            [
                new SchemaChange(
                    SchemaChangeKind.OwnershipConflict,
                    $"{destination.Schema}.{destination.Name}",
                    "A matching destination table exists but is not managed by Replicera.",
                    false,
                    true)
            ]);
        }

        var changes = new List<SchemaChange>();
        var destinationColumns = destination.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var sourceColumns = source.Columns
            .Where(column => column.IsSupported)
            .ToDictionary(column => column.LogicalName, StringComparer.OrdinalIgnoreCase);

        foreach (var sourceColumn in source.Columns.Where(column => column.IsSupported))
        {
            if (!destinationColumns.TryGetValue(sourceColumn.LogicalName, out var destinationColumn))
            {
                var canAdd = sourceColumn.IsSupported && sourceColumn.IsNullable;
                changes.Add(new SchemaChange(
                    canAdd ? SchemaChangeKind.AddColumn : SchemaChangeKind.IncompatibleColumn,
                    sourceColumn.LogicalName,
                    canAdd
                        ? $"Add column '{sourceColumn.LogicalName}'."
                        : sourceColumn.UnsupportedReason ?? "A required column cannot be added safely without backfilling existing rows.",
                    canAdd && policy.AddColumns == SchemaAction.Automatic,
                    !canAdd || policy.AddColumns != SchemaAction.Automatic));
                continue;
            }

            if (sourceColumn.SourceType != destinationColumn.SourceType)
            {
                changes.Add(Incompatible(sourceColumn.LogicalName, "Source and destination types differ."));
                continue;
            }

            if (RequiresExpansion(sourceColumn, destinationColumn))
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.ExpandColumn,
                    sourceColumn.LogicalName,
                    $"Expand column '{sourceColumn.LogicalName}' to fit the source definition.",
                    policy.ExpandCompatibleColumns == SchemaAction.Automatic,
                    policy.ExpandCompatibleColumns != SchemaAction.Automatic));
            }
            else if (IsNarrowingOrIncompatible(sourceColumn, destinationColumn))
            {
                changes.Add(Incompatible(sourceColumn.LogicalName, "The source change is narrowing or incompatible."));
            }
        }

        foreach (var destinationColumn in destination.Columns.Where(column => !sourceColumns.ContainsKey(column.Name)))
        {
            changes.Add(new SchemaChange(
                SchemaChangeKind.SourceColumnRemoved,
                destinationColumn.Name,
                $"Source column '{destinationColumn.Name}' no longer exists; retain it in the destination.",
                false,
                false));
        }

        return new SchemaPlan(changes);
    }

    private static bool RequiresExpansion(ColumnDefinition source, DestinationColumn destination)
    {
        return source.SourceType switch
        {
            SourceType.String => Length(source.MaxLength) > Length(destination.MaxLength),
            SourceType.Decimal or SourceType.Money =>
                IntegerDigits(source.Precision, source.Scale) >= IntegerDigits(destination.Precision, destination.Scale)
                && Value(source.Scale) >= Value(destination.Scale)
                && (source.Precision != destination.Precision || source.Scale != destination.Scale),
            _ => false
        };
    }

    private static bool IsNarrowingOrIncompatible(ColumnDefinition source, DestinationColumn destination)
    {
        return source.SourceType switch
        {
            SourceType.String => Length(source.MaxLength) < Length(destination.MaxLength),
            SourceType.Decimal or SourceType.Money =>
                IntegerDigits(source.Precision, source.Scale) < IntegerDigits(destination.Precision, destination.Scale)
                || Value(source.Scale) < Value(destination.Scale),
            _ => source.IsNullable != destination.IsNullable && !source.IsNullable
        };
    }

    private static SchemaChange Incompatible(string name, string reason) => new(
        SchemaChangeKind.IncompatibleColumn,
        name,
        reason,
        false,
        true);

    private static int Length(int? length) => length ?? int.MaxValue;

    private static int Value(int? value) => value ?? 0;

    private static int IntegerDigits(int? precision, int? scale) => Value(precision) - Value(scale);
}
