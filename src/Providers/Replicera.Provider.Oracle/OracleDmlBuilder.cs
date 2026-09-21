using Replicera.Core.Models;

namespace Replicera.Provider.Oracle;

public static class OracleDmlBuilder
{
    public const string OperationColumn = "__REPLICERA_OPERATION";

    public static string BuildCreateStaging(TableDefinition table, string stagingTable)
    {
        ArgumentNullException.ThrowIfNull(table);
        var staging = OracleIdentifier.Quote(OracleIdentifier.Normalize(stagingTable));
        var columns = OracleTableLayout.GetColumns(table)
            .Select(column =>
            {
                var type = column.IsLookupTarget
                    ? "NVARCHAR2(128)"
                    : OracleTypeMapper.Map(column.Source).Declaration;
                return $"{OracleIdentifier.Quote(column.Name)} {type} NULL";
            })
            .Append($"{OracleIdentifier.Quote(OperationColumn)} CHAR(1) NOT NULL");
        return $"CREATE GLOBAL TEMPORARY TABLE {staging} ({string.Join(", ", columns)}) ON COMMIT DELETE ROWS";
    }

    public static string BuildApplyStaging(TableDefinition table, string stagingTable)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = OracleTableLayout.GetColumns(table);
        var primaryKey = columns.Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget);
        var mutable = columns.Where(column => !column.Source.IsPrimaryKey).ToArray();
        var target = OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName));
        var staging = OracleIdentifier.Quote(OracleIdentifier.Normalize(stagingTable));
        var key = OracleIdentifier.Quote(primaryKey.Name);
        var operation = OracleIdentifier.Quote(OperationColumn);
        var columnList = string.Join(", ", columns.Select(column => OracleIdentifier.Quote(column.Name)));
        var valueList = string.Join(", ", columns.Select(column => $"source.{OracleIdentifier.Quote(column.Name)}"));
        var matched = mutable.Length == 0
            ? string.Empty
            : Environment.NewLine + "WHEN MATCHED THEN UPDATE SET " + string.Join(", ", mutable.Select(column =>
                $"target.{OracleIdentifier.Quote(column.Name)} = source.{OracleIdentifier.Quote(column.Name)}"));
        return $"""
            MERGE INTO {target} target
            USING (SELECT * FROM {staging} WHERE {operation} = 'U') source
            ON (target.{key} = source.{key}){matched}
            WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({valueList})
            """;
    }

    public static string BuildDeleteStagingChanges(TableDefinition table, string stagingTable)
    {
        var primaryKey = OracleTableLayout.GetColumns(table)
            .Single(column => column.Source.IsPrimaryKey && !column.IsLookupTarget);
        var target = OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName));
        var staging = OracleIdentifier.Quote(OracleIdentifier.Normalize(stagingTable));
        var key = OracleIdentifier.Quote(primaryKey.Name);
        var operation = OracleIdentifier.Quote(OperationColumn);
        return $"DELETE FROM {target} target WHERE EXISTS (SELECT 1 FROM {staging} source WHERE source.{operation} = 'D' AND source.{key} = target.{key})";
    }

    public static string BuildClearStaging(string stagingTable) =>
        $"DELETE FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(stagingTable))}";

    public static string BuildDropStaging(string stagingTable) =>
        $"DROP TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(stagingTable))}";
}
