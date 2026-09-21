using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql;

public sealed record PostgreSqlPhysicalColumn(string Name, ColumnDefinition Source, bool IsLookupTarget = false);

public static class PostgreSqlTableLayout
{
    public static IReadOnlyList<PostgreSqlPhysicalColumn> GetColumns(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var supported = table.Columns.Where(column => column.IsSupported).ToArray();
        var sourceNames = supported.Select(column => column.LogicalName).ToList();
        sourceNames.AddRange(supported.Where(IsPolymorphicLookup).Select(column => $"{column.LogicalName}_type"));
        var names = PostgreSqlIdentifier.NormalizeDistinct(sourceNames);
        var result = new List<PostgreSqlPhysicalColumn>();
        foreach (var column in supported)
        {
            result.Add(new(names[column.LogicalName], column));
            if (IsPolymorphicLookup(column))
            {
                result.Add(new(names[$"{column.LogicalName}_type"], column, true));
            }
        }

        return result;
    }

    private static bool IsPolymorphicLookup(ColumnDefinition column) =>
        column.SourceType == SourceType.Lookup && column.LookupTargets.Count > 1;
}
