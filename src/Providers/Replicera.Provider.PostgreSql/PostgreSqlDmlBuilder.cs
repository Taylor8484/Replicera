using Replicera.Core.Models;
using Replicera.Core.Schema;

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

    public static string BuildApplyStaging(
        TableDefinition table,
        string stagingTable,
        string schema = "public",
        bool retainDeletedRows = false)
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
        var updates = mutable.Select(column =>
                $"{PostgreSqlIdentifier.Quote(column.Name)} = EXCLUDED.{PostgreSqlIdentifier.Quote(column.Name)}")
            .Append($"{PostgreSqlIdentifier.Quote(ManagedColumnNames.DataLoadDate)} = CURRENT_TIMESTAMP")
            .Concat(retainDeletedRows
                ? [$"{PostgreSqlIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)} = NULL"]
                : []);
        var insertColumns = $"{columnList}, {PostgreSqlIdentifier.Quote(ManagedColumnNames.DataLoadDate)}"
            + (retainDeletedRows ? $", {PostgreSqlIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)}" : string.Empty);
        var insertValues = $"{sourceList}, CURRENT_TIMESTAMP" + (retainDeletedRows ? ", NULL" : string.Empty);
        var deletion = retainDeletedRows
            ? $"""
              UPDATE {target} AS target
              SET {PostgreSqlIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)} =
                  COALESCE(target.{PostgreSqlIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)}, CURRENT_TIMESTAMP)
              FROM {staging} AS source
              WHERE source.{operation} = 'D'
                AND target.{key} = source.{key};
              """
            : $"""
              DELETE FROM {target} AS target
              USING {staging} AS source
              WHERE source.{operation} = 'D'
                AND target.{key} = source.{key};
              """;
        return $"""
            INSERT INTO {target} ({insertColumns})
            SELECT {insertValues}
            FROM {staging} AS source
            WHERE source.{operation} = 'U'
            ON CONFLICT ({key}) DO UPDATE SET {string.Join(", ", updates)};

            {deletion}
            """;
    }

    public static string BuildTruncateStaging(string stagingTable) =>
        $"TRUNCATE TABLE {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(stagingTable))};";

    public static string BuildDropStaging(string stagingTable) =>
        $"DROP TABLE {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(stagingTable))};";
}
