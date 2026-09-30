using Replicera.Core.Models;
using Replicera.Core.Schema;

namespace Replicera.Provider.SqlServer;

public static class SqlServerDmlBuilder
{
    public const string OperationColumn = "__replicera_operation";

    /// <summary>
    /// Names a session temporary staging table. Temporary tables need no permission in the
    /// destination database and are removed by SQL Server when the connection closes, including
    /// after a crash, so no staging objects are left behind.
    /// </summary>
    public static string StagingTableName(Guid runId) => $"#replicera_stage_{runId:N}";

    public static string BuildCreateStaging(TableDefinition table, string stagingTable, string schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(table);
        var target = Qualified(schema, table.DestinationName);
        var staging = Staging(stagingTable);
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
                // Redefined text columns would otherwise take the tempdb collation.
                var collation = IsText(sqlType) ? " COLLATE DATABASE_DEFAULT" : string.Empty;
                return $"ALTER TABLE {staging} ALTER COLUMN {SqlServerIdentifier.Quote(column.Name)} {sqlType}{collation} NULL;";
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
        var staging = Staging(stagingTable);
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

    public static string BuildDropStaging(string stagingTable) =>
        $"DROP TABLE {Staging(stagingTable)};";

    public static string BuildTruncateStaging(string stagingTable) =>
        $"TRUNCATE TABLE {Staging(stagingTable)};";

    internal static string Staging(string stagingTable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingTable);
        if (!stagingTable.StartsWith('#') || stagingTable.StartsWith("##", StringComparison.Ordinal))
        {
            throw new ArgumentException("Staging tables must be session temporary tables.", nameof(stagingTable));
        }

        return SqlServerIdentifier.Quote(stagingTable);
    }

    private static bool IsText(string sqlType) =>
        sqlType.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase)
        || sqlType.StartsWith("nchar", StringComparison.OrdinalIgnoreCase)
        || sqlType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase)
        || sqlType.StartsWith("char", StringComparison.OrdinalIgnoreCase);

    private static string Qualified(string schema, string table) =>
        $"{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(schema))}.{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table))}";
}
