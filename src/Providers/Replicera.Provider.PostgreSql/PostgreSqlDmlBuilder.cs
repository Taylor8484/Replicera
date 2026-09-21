using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql;

public static class PostgreSqlDmlBuilder
{
    public const string OperationColumn = "__replicera_operation";

    public static string BuildCreateStaging(TableDefinition table, string stagingTable, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(table);
        var staging = PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(stagingTable));
        var target = PostgreSqlIdentifier.Qualified(schema, table.DestinationName);
        var statements = new List<string>
        {
            $"CREATE TEMP TABLE {staging} (LIKE {target}) ON COMMIT DROP;",
            $"ALTER TABLE {staging} ADD COLUMN {PostgreSqlIdentifier.Quote(OperationColumn)} character(1) NOT NULL;"
        };
        statements.AddRange(PostgreSqlTableLayout.GetColumns(table)
            .Where(column => !column.Source.IsPrimaryKey)
            .Select(column => $"ALTER TABLE {staging} ALTER COLUMN {PostgreSqlIdentifier.Quote(column.Name)} DROP NOT NULL;"));
        return string.Join(Environment.NewLine, statements);
    }

    public static string BuildApplyStaging(TableDefinition table, string stagingTable, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = PostgreSqlTableLayout.GetColumns(table);
        var primaryKey = columns.Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget);
        var mutable = columns.Where(column => !column.Source.IsPrimaryKey).ToArray();
        var target = PostgreSqlIdentifier.Qualified(schema, table.DestinationName);
        var staging = PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(stagingTable));
        var key = PostgreSqlIdentifier.Quote(primaryKey.Name);
        var operation = PostgreSqlIdentifier.Quote(OperationColumn);
        var columnList = string.Join(", ", columns.Select(column => PostgreSqlIdentifier.Quote(column.Name)));
        var sourceList = string.Join(", ", columns.Select(column => $"source.{PostgreSqlIdentifier.Quote(column.Name)}"));
        var conflictAction = mutable.Length == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", mutable.Select(column =>
                $"{PostgreSqlIdentifier.Quote(column.Name)} = EXCLUDED.{PostgreSqlIdentifier.Quote(column.Name)}"));
        return $"""
            INSERT INTO {target} ({columnList})
            SELECT {sourceList}
            FROM {staging} AS source
            WHERE source.{operation} = 'U'
            ON CONFLICT ({key}) {conflictAction};

            DELETE FROM {target} AS target
            USING {staging} AS source
            WHERE source.{operation} = 'D'
              AND target.{key} = source.{key};
            """;
    }

    public static string BuildTruncateStaging(string stagingTable) =>
        $"TRUNCATE TABLE {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(stagingTable))};";

    public static string BuildDropStaging(string stagingTable) =>
        $"DROP TABLE {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(stagingTable))};";
}
