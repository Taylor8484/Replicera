using Replicera.Core.Models;

namespace Replicera.Provider.Oracle;

public static class OracleDdlBuilder
{
    public static string BuildCreateTable(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var definitions = new List<string>();
        var layout = OracleTableLayout.GetColumns(table);
        foreach (var physicalColumn in layout)
        {
            var type = physicalColumn.IsLookupTarget
                ? "NVARCHAR2(128)"
                : OracleTypeMapper.Map(physicalColumn.Source).Declaration;
            var nullability = physicalColumn.Source.IsNullable && !physicalColumn.Source.IsPrimaryKey
                ? "NULL"
                : "NOT NULL";
            definitions.Add($"    {OracleIdentifier.Quote(physicalColumn.Name)} {type} {nullability}");
        }

        var primaryKey = layout.Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget);
        var constraint = OracleIdentifier.Normalize($"PK_{table.DestinationName}");
        definitions.Add($"    CONSTRAINT {OracleIdentifier.Quote(constraint)} PRIMARY KEY ({OracleIdentifier.Quote(primaryKey.Name)})");
        return $"CREATE TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))}\n(\n{string.Join(",\n", definitions)}\n)";
    }
}
