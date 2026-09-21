using Replicera.Core.Configuration;
using Replicera.Core.Models;

namespace Replicera.Core.Schema;

public static class SchemaPlanner
{
    public static SchemaPlan Plan(
        TableDefinition source,
        DestinationTable? destination,
        SchemaPolicy policy,
        SynchronizationMode mode = SynchronizationMode.Complete)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);

        var managedNameCollision = source.Columns.FirstOrDefault(column =>
            string.Equals(column.LogicalName, ManagedColumnNames.DataLoadDate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.LogicalName, ManagedColumnNames.SourceRemoveDate, StringComparison.OrdinalIgnoreCase));
        if (managedNameCollision is not null)
        {
            return new SchemaPlan(
            [
                new SchemaChange(
                    SchemaChangeKind.IncompatibleColumn,
                    managedNameCollision.LogicalName,
                    $"Source column '{managedNameCollision.LogicalName}' conflicts with a Replicera-managed column.",
                    false,
                    true)
            ]);
        }

        if (destination is null)
        {
            var createChanges = new List<SchemaChange>
            {
                new SchemaChange(
                    SchemaChangeKind.CreateTable,
                    source.DestinationName,
                    $"Create destination table for '{source.LogicalName}'.",
                    policy.CreateTables == SchemaAction.Automatic,
                    policy.CreateTables != SchemaAction.Automatic)
            };
            AddManagedColumns(createChanges, [], mode);
            return new SchemaPlan(createChanges);
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

        if (mode == SynchronizationMode.Reload)
        {
            var recreateChanges = new List<SchemaChange>
            {
                new(
                    SchemaChangeKind.RecreateTable,
                    source.DestinationName,
                    $"Drop and recreate destination table for '{source.LogicalName}'.",
                    true,
                    false)
            };
            AddManagedColumns(recreateChanges, [], mode);
            return new SchemaPlan(recreateChanges);
        }

        var changes = new List<SchemaChange>();
        var destinationColumns = destination.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var tableRenames = policy.ColumnRenames
            .FirstOrDefault(pair => string.Equals(pair.Key, source.LogicalName, StringComparison.OrdinalIgnoreCase))
            .Value ?? new Dictionary<string, string>();
        var renamedDestinationColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rename in tableRenames)
        {
            var sourceColumn = source.Columns.FirstOrDefault(column =>
                column.IsSupported && string.Equals(column.LogicalName, rename.Value, StringComparison.OrdinalIgnoreCase));
            var sourceStillHasOldName = source.Columns.Any(column =>
                string.Equals(column.LogicalName, rename.Key, StringComparison.OrdinalIgnoreCase));
            if (sourceColumn is not null
                && !sourceStillHasOldName
                && !destinationColumns.ContainsKey(rename.Key)
                && destinationColumns.ContainsKey(rename.Value))
            {
                continue;
            }

            if (sourceColumn is null
                || sourceStillHasOldName
                || !destinationColumns.TryGetValue(rename.Key, out var oldDestinationColumn)
                || destinationColumns.ContainsKey(rename.Value)
                || IsManagedName(rename.Key)
                || IsManagedName(rename.Value))
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.IncompatibleColumn,
                    rename.Key,
                    $"Cannot apply configured rename '{rename.Key}' to '{rename.Value}' for table '{source.LogicalName}'. The old name must exist only in the destination and the new name must exist only in the source.",
                    false,
                    true,
                    rename.Value));
                continue;
            }

            destinationColumns[rename.Value] = oldDestinationColumn with { Name = rename.Value };
            renamedDestinationColumns.Add(rename.Key);
            changes.Add(new SchemaChange(
                SchemaChangeKind.RenameColumn,
                rename.Key,
                $"Rename destination column '{rename.Key}' to '{rename.Value}'.",
                true,
                false,
                rename.Value));
        }
        var sourceColumnNames = source.Columns
            .Where(column => column.IsSupported)
            .Select(column => column.LogicalName)
            .Concat(source.Columns
                .Where(column => column.IsSupported
                    && column.SourceType == SourceType.Lookup
                    && column.LookupTargets.Count > 1)
                .Select(column => $"{column.LogicalName}_type"))
            .Append(ManagedColumnNames.DataLoadDate)
            .Concat(mode == SynchronizationMode.NoDataLoss ? [ManagedColumnNames.SourceRemoveDate] : [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddManagedColumns(changes, destinationColumns.Keys, mode);

        foreach (var sourceColumn in source.Columns.Where(column => column.IsSupported))
        {
            if (!destinationColumns.TryGetValue(sourceColumn.LogicalName, out var destinationColumn))
            {
                var canAdd = sourceColumn.IsSupported;
                changes.Add(new SchemaChange(
                    canAdd ? SchemaChangeKind.AddColumn : SchemaChangeKind.IncompatibleColumn,
                    sourceColumn.LogicalName,
                    canAdd
                        ? $"Add column '{sourceColumn.LogicalName}'."
                        : sourceColumn.UnsupportedReason ?? "The column cannot be added safely.",
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

        foreach (var lookup in source.Columns.Where(column =>
                     column.IsSupported
                     && column.SourceType == SourceType.Lookup
                     && column.LookupTargets.Count > 1
                     && destinationColumns.ContainsKey(column.LogicalName)
                     && !destinationColumns.ContainsKey($"{column.LogicalName}_type")))
        {
            changes.Add(new SchemaChange(
                SchemaChangeKind.AddLookupTypeColumn,
                lookup.LogicalName,
                $"Add polymorphic lookup type column for '{lookup.LogicalName}'.",
                true,
                false));
        }

        foreach (var destinationColumn in destination.Columns.Where(column =>
                     !sourceColumnNames.Contains(column.Name)
                     && !renamedDestinationColumns.Contains(column.Name)))
        {
            var drop = mode is SynchronizationMode.Complete or SynchronizationMode.Reload;
            changes.Add(new SchemaChange(
                drop ? SchemaChangeKind.DropColumn : SchemaChangeKind.SourceColumnRemoved,
                destinationColumn.Name,
                drop
                    ? $"Drop destination column '{destinationColumn.Name}' because it no longer exists in the source."
                    : $"Source column '{destinationColumn.Name}' no longer exists; retain it in the destination.",
                drop,
                false));
        }

        return new SchemaPlan(changes);
    }

    private static bool IsManagedName(string name) =>
        string.Equals(name, ManagedColumnNames.DataLoadDate, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ManagedColumnNames.SourceRemoveDate, StringComparison.OrdinalIgnoreCase);

    private static void AddManagedColumns(
        List<SchemaChange> changes,
        IEnumerable<string> destinationColumnNames,
        SynchronizationMode mode)
    {
        var existing = destinationColumnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Add(ManagedColumnNames.DataLoadDate, "Track when each destination row was last loaded.");
        if (mode == SynchronizationMode.NoDataLoss)
        {
            Add(ManagedColumnNames.SourceRemoveDate, "Track when a retained row was removed from the source.");
        }

        void Add(string name, string description)
        {
            if (!existing.Contains(name))
            {
                changes.Add(new SchemaChange(SchemaChangeKind.AddManagedColumn, name, description, true, false));
            }
        }
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
            _ => false
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
