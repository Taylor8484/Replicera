using Replicera.Core.Models;
using Replicera.Core.Schema;

namespace Replicera.Provider.SqlServer;

public static class SqlServerDmlBuilder
{
    public const string OperationColumn = "__replicera_operation";

    public static string BuildCreateStaging(TableDefinition table, string stagingTable, string schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(table);
        var target = Qualified(schema, table.DestinationName);
        var staging = Qualified(schema, stagingTable);
        var statements = new List<string>
        {
            $"SELECT TOP (0) *, CAST(NULL AS char(1)) AS {SqlServerIdentifier.Quote(OperationColumn)} INTO {staging} FROM {target};"
        };
        statements.AddRange(SqlServerTableLayout.GetColumns(table)
            .Where(column => !column.Source.IsPrimaryKey)
            .Select(column =>
            {
                var sqlType = column.IsLookupTarget
                    ? "nvarchar(128)"
                    : SqlServerTypeMapper.Map(column.Source).Declaration;
                return $"ALTER TABLE {staging} ALTER COLUMN {SqlServerIdentifier.Quote(column.Name)} {sqlType} NULL;";
            }));
        return string.Join(Environment.NewLine, statements);
    }

    public static string BuildApplyStaging(
        TableDefinition table,
        string stagingTable,
        string schema = "dbo",
        bool retainDeletedRows = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = SqlServerTableLayout.GetColumns(table);
        var primaryKey = columns.Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget);
        var mutable = columns.Where(column => !column.Source.IsPrimaryKey).ToArray();
        var target = Qualified(schema, table.DestinationName);
        var staging = Qualified(schema, stagingTable);
        var quotedPrimaryKey = SqlServerIdentifier.Quote(primaryKey.Name);
        var operation = SqlServerIdentifier.Quote(OperationColumn);
        var statements = new List<string>();

        var assignments = mutable.Select(column =>
                $"target.{SqlServerIdentifier.Quote(column.Name)} = source.{SqlServerIdentifier.Quote(column.Name)}")
            .Append($"target.{SqlServerIdentifier.Quote(ManagedColumnNames.DataLoadDate)} = SYSUTCDATETIME()")
            .Concat(retainDeletedRows
                ? [$"target.{SqlServerIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)} = NULL"]
                : []);
        statements.Add($"""
            UPDATE target
            SET {string.Join(",\n    ", assignments)}
            FROM {target} AS target
            INNER JOIN {staging} AS source ON source.{quotedPrimaryKey} = target.{quotedPrimaryKey}
            WHERE source.{operation} = 'U';
            """);

        var columnList = string.Join(", ", columns.Select(column => SqlServerIdentifier.Quote(column.Name)));
        var sourceList = string.Join(", ", columns.Select(column => $"source.{SqlServerIdentifier.Quote(column.Name)}"));
        var insertColumns = $"{columnList}, {SqlServerIdentifier.Quote(ManagedColumnNames.DataLoadDate)}"
            + (retainDeletedRows ? $", {SqlServerIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)}" : string.Empty);
        var insertValues = $"{sourceList}, SYSUTCDATETIME()" + (retainDeletedRows ? ", NULL" : string.Empty);
        statements.Add($"""
            INSERT INTO {target} ({insertColumns})
            SELECT {insertValues}
            FROM {staging} AS source
            WHERE source.{operation} = 'U'
              AND NOT EXISTS (SELECT 1 FROM {target} AS target WHERE target.{quotedPrimaryKey} = source.{quotedPrimaryKey});
            """);
        statements.Add(retainDeletedRows
            ? $"""
              UPDATE target
              SET target.{SqlServerIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)} =
                  COALESCE(target.{SqlServerIdentifier.Quote(ManagedColumnNames.SourceRemoveDate)}, SYSUTCDATETIME())
              FROM {target} AS target
              INNER JOIN {staging} AS source ON source.{quotedPrimaryKey} = target.{quotedPrimaryKey}
              WHERE source.{operation} = 'D';
              """
            : $"""
              DELETE target
              FROM {target} AS target
              INNER JOIN {staging} AS source ON source.{quotedPrimaryKey} = target.{quotedPrimaryKey}
              WHERE source.{operation} = 'D';
              """);
        return string.Join(Environment.NewLine, statements);
    }

    public static string BuildDropStaging(string stagingTable, string schema = "dbo") =>
        $"DROP TABLE {Qualified(schema, stagingTable)};";

    private static string Qualified(string schema, string table) =>
        $"{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(schema))}.{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table))}";
}
