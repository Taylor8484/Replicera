using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer;

public static class SqlServerDdlBuilder
{
    public static string BuildCreateTable(TableDefinition table, string schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var definitions = new List<string>();
        foreach (var physicalColumn in SqlServerTableLayout.GetColumns(table))
        {
            var column = physicalColumn.Source;
            if (!column.IsSupported)
            {
                throw new NotSupportedException(column.UnsupportedReason);
            }

            var sqlType = physicalColumn.IsLookupTarget
                ? "nvarchar(128)"
                : SqlServerTypeMapper.Map(column).Declaration;
            var nullability = column.IsNullable && !column.IsPrimaryKey ? "NULL" : "NOT NULL";
            definitions.Add($"    {SqlServerIdentifier.Quote(physicalColumn.Name)} {sqlType} {nullability}");
        }

        var constraintName = SqlServerIdentifier.Normalize($"PK_{schema}_{table.DestinationName}");
        var primaryKey = SqlServerTableLayout.GetColumns(table)
            .Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget);
        definitions.Add(
            $"    CONSTRAINT {SqlServerIdentifier.Quote(constraintName)} PRIMARY KEY ({SqlServerIdentifier.Quote(primaryKey.Name)})");

        return $"CREATE TABLE {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(schema))}.{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))}\n(\n{string.Join(",\n", definitions)}\n);";
    }
}
