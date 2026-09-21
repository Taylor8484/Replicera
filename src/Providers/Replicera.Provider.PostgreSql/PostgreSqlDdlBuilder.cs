using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql;

public static class PostgreSqlDdlBuilder
{
    public static string BuildCreateTable(TableDefinition table, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var definitions = new List<string>();
        var layout = PostgreSqlTableLayout.GetColumns(table);
        foreach (var physicalColumn in layout)
        {
            var type = physicalColumn.IsLookupTarget
                ? "character varying(128)"
                : PostgreSqlTypeMapper.Map(physicalColumn.Source).Declaration;
            var nullability = physicalColumn.Source.IsNullable && !physicalColumn.Source.IsPrimaryKey
                ? "NULL"
                : "NOT NULL";
            definitions.Add($"    {PostgreSqlIdentifier.Quote(physicalColumn.Name)} {type} {nullability}");
        }

        var primaryKey = layout.Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget);
        var constraint = PostgreSqlIdentifier.Normalize($"pk_{schema}_{table.DestinationName}");
        definitions.Add($"    CONSTRAINT {PostgreSqlIdentifier.Quote(constraint)} PRIMARY KEY ({PostgreSqlIdentifier.Quote(primaryKey.Name)})");
        return $"CREATE TABLE {PostgreSqlIdentifier.Qualified(schema, table.DestinationName)}\n(\n{string.Join(",\n", definitions)}\n);";
    }
}
