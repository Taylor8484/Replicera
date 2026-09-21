using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer;

public sealed record SqlServerPhysicalColumn(
    string Name,
    ColumnDefinition Source,
    bool IsLookupTarget = false);

public static class SqlServerTableLayout
{
    public static IReadOnlyList<SqlServerPhysicalColumn> GetColumns(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var supportedColumns = table.Columns.Where(column => column.IsSupported).ToArray();
        var sourceNames = supportedColumns.Select(column => column.LogicalName).ToList();
        sourceNames.AddRange(supportedColumns
            .Where(IsPolymorphicLookup)
            .Select(column => $"{column.LogicalName}_type"));
        var names = SqlServerIdentifier.NormalizeDistinct(sourceNames);

        var result = new List<SqlServerPhysicalColumn>();
        foreach (var column in supportedColumns)
        {
            result.Add(new SqlServerPhysicalColumn(names[column.LogicalName], column));
            if (IsPolymorphicLookup(column))
            {
                result.Add(new SqlServerPhysicalColumn(names[$"{column.LogicalName}_type"], column, true));
            }
        }

        return result;
    }

    private static bool IsPolymorphicLookup(ColumnDefinition column) =>
        column.SourceType == SourceType.Lookup && column.LookupTargets.Count > 1;
}
