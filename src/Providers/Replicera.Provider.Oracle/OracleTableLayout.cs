using Replicera.Core.Models;

namespace Replicera.Provider.Oracle;

public sealed record OraclePhysicalColumn(string Name, ColumnDefinition Source, bool IsLookupTarget = false);

public static class OracleTableLayout
{
    public static IReadOnlyList<OraclePhysicalColumn> GetColumns(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var supported = table.Columns.Where(column => column.IsSupported).ToArray();
        var sourceNames = supported.Select(column => column.LogicalName).ToList();
        sourceNames.AddRange(supported.Where(IsPolymorphicLookup).Select(column => $"{column.LogicalName}_type"));
        var names = OracleIdentifier.NormalizeDistinct(sourceNames);
        var result = new List<OraclePhysicalColumn>();
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
