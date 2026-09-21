using Microsoft.Data.SqlClient;
using Replicera.Core.Abstractions;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Core.Schema;

namespace Replicera.Provider.SqlServer;

public sealed class SqlServerSchemaManager(string connectionString) : IDestinationSchemaManager
{
    public async Task<DestinationTable?> ReadTableAsync(
        TableDefinition source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText = "SELECT CONVERT(bit, CASE WHEN OBJECT_ID(N'[replicera].[Tables]', N'U') IS NULL THEN 0 ELSE 1 END);";
        var hasMetadata = (bool)(await metadataCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? false);
        await using var command = connection.CreateCommand();
        command.CommandText = hasMetadata
            ? """
            SELECT c.[name], t.[name], c.[max_length], c.[precision], c.[scale], c.[is_nullable],
                   CASE WHEN managed.[TableId] IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END
            FROM sys.tables AS tbl
            INNER JOIN sys.schemas AS s ON s.[schema_id] = tbl.[schema_id]
            INNER JOIN sys.columns AS c ON c.[object_id] = tbl.[object_id]
            INNER JOIN sys.types AS t ON t.[user_type_id] = c.[user_type_id]
            LEFT JOIN [replicera].[Tables] AS managed
              ON managed.[DestinationSchema] = s.[name] AND managed.[DestinationTableName] = tbl.[name]
            WHERE s.[name] = N'dbo' AND tbl.[name] = @table
            ORDER BY c.[column_id];
            """
            : """
            SELECT c.[name], t.[name], c.[max_length], c.[precision], c.[scale], c.[is_nullable], CAST(0 AS bit)
            FROM sys.tables AS tbl
            INNER JOIN sys.schemas AS s ON s.[schema_id] = tbl.[schema_id]
            INNER JOIN sys.columns AS c ON c.[object_id] = tbl.[object_id]
            INNER JOIN sys.types AS t ON t.[user_type_id] = c.[user_type_id]
            WHERE s.[name] = N'dbo' AND tbl.[name] = @table
            ORDER BY c.[column_id];
            """;
        _ = command.Parameters.AddWithValue("@table", SqlServerIdentifier.Normalize(source.DestinationName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<DestinationColumn>();
        var managed = false;
        var sourceByName = source.Columns.ToDictionary(
            column => SqlServerIdentifier.Normalize(column.LogicalName),
            StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            managed = reader.GetBoolean(6);
            if (name.EndsWith("_type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sqlType = reader.GetString(1);
            var matched = sourceByName.GetValueOrDefault(name);
            var type = matched is not null && IsCompatibleSqlType(matched.SourceType, sqlType)
                ? matched.SourceType
                : InferSourceType(sqlType);
            var maxLength = reader.GetInt16(2);
            columns.Add(new DestinationColumn(
                name,
                type,
                reader.GetBoolean(5),
                maxLength switch
                {
                    -1 => null,
                    _ when reader.GetString(1) is "nvarchar" or "nchar" => maxLength / 2,
                    _ => maxLength
                },
                reader.GetByte(3),
                reader.GetByte(4)));
        }

        return columns.Count == 0
            ? null
            : new DestinationTable("dbo", SqlServerIdentifier.Normalize(source.DestinationName), columns, managed);
    }

    public async Task ApplySchemaPlanAsync(
        string jobName,
        TableDefinition source,
        SchemaPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HasBlockingChanges)
        {
            throw new RepliceraException(ErrorCategory.SchemaConflict, "Schema plan contains blocking changes.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in plan.Changes.Where(change => change.IsAutomatic))
        {
            var sql = change.Kind switch
            {
                SchemaChangeKind.CreateTable => SqlServerDdlBuilder.BuildCreateTable(source),
                SchemaChangeKind.AddColumn => BuildAddColumn(source, change.ObjectName),
                SchemaChangeKind.ExpandColumn => BuildAlterColumn(source, change.ObjectName),
                _ => null
            };
            if (sql is null)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
#pragma warning disable CA2100 // SQL is generated from normalized and quoted provider-owned identifiers.
            command.CommandText = sql;
#pragma warning restore CA2100
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var tableId = await EnsureOwnershipAsync(
            connection,
            transaction,
            jobName,
            source,
            cancellationToken).ConfigureAwait(false);
        foreach (var change in plan.Changes)
        {
            await RecordSchemaChangeAsync(
                connection,
                transaction,
                tableId,
                change,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid> EnsureOwnershipAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string jobName,
        TableDefinition table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @tableId uniqueidentifier;
            SELECT @tableId = [TableId]
            FROM [replicera].[Tables] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ReplicationJobId] = @jobName AND [DataverseLogicalName] = @logicalName;

            IF @tableId IS NULL
            BEGIN
                SET @tableId = NEWID();
                INSERT INTO [replicera].[Tables]
                (
                    [TableId], [ReplicationJobId], [DataverseLogicalName], [DataverseEntitySetName],
                    [DestinationSchema], [DestinationTableName], [PrimaryKey], [ChangeTrackingEnabled], [Status]
                )
                VALUES
                (
                    @tableId, @jobName, @logicalName, @entitySetName,
                    N'dbo', @destinationName, @primaryKey, 1, N'Uninitialized'
                );
            END;

            SELECT @tableId;
            """;
        _ = command.Parameters.AddWithValue("@jobName", jobName);
        _ = command.Parameters.AddWithValue("@logicalName", table.LogicalName);
        _ = command.Parameters.AddWithValue("@entitySetName", table.EntitySetName);
        _ = command.Parameters.AddWithValue("@destinationName", SqlServerIdentifier.Normalize(table.DestinationName));
        _ = command.Parameters.AddWithValue("@primaryKey", table.PrimaryKey.LogicalName);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SQL Server did not return the managed table ID."));
    }

    private static async Task RecordSchemaChangeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid tableId,
        SchemaChange change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO [replicera].[SchemaHistory]
                ([SchemaHistoryId], [TableId], [DetectedUtc], [AppliedUtc], [ChangeKind], [ObjectName], [Description], [Status])
            VALUES
                (NEWID(), @tableId, SYSUTCDATETIME(), CASE WHEN @applied = 1 THEN SYSUTCDATETIME() ELSE NULL END,
                 @changeKind, @objectName, @description, CASE WHEN @applied = 1 THEN N'Applied' ELSE N'Detected' END);
            """;
        _ = command.Parameters.AddWithValue("@tableId", tableId);
        _ = command.Parameters.AddWithValue("@applied", change.IsAutomatic);
        _ = command.Parameters.AddWithValue("@changeKind", change.Kind.ToString());
        _ = command.Parameters.AddWithValue("@objectName", change.ObjectName);
        _ = command.Parameters.AddWithValue("@description", change.Description);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAddColumn(TableDefinition table, string logicalName)
    {
        var column = table.Columns.Single(column => string.Equals(column.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        var type = SqlServerTypeMapper.Map(column).Declaration;
        var nullable = column.IsNullable ? "NULL" : "NOT NULL";
        var tableName = $"[dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))}";
        var sql = $"ALTER TABLE {tableName} ADD {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(column.LogicalName))} {type} {nullable};";
        if (column.SourceType == SourceType.Lookup && column.LookupTargets.Count > 1)
        {
            sql += $"{Environment.NewLine}ALTER TABLE {tableName} ADD {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize($"{column.LogicalName}_type"))} nvarchar(128) {nullable};";
        }

        return sql;
    }

    private static string BuildAlterColumn(TableDefinition table, string logicalName)
    {
        var column = table.Columns.Single(column => string.Equals(column.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        var type = SqlServerTypeMapper.Map(column).Declaration;
        var nullable = column.IsNullable ? "NULL" : "NOT NULL";
        return $"ALTER TABLE [dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))} ALTER COLUMN {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(column.LogicalName))} {type} {nullable};";
    }

    private static SourceType InferSourceType(string sqlType) => sqlType switch
    {
        "uniqueidentifier" => SourceType.Guid,
        "bit" => SourceType.Boolean,
        "int" => SourceType.Int32,
        "bigint" => SourceType.Int64,
        "decimal" or "numeric" or "money" or "smallmoney" => SourceType.Decimal,
        "float" or "real" => SourceType.Double,
        "date" or "datetime" or "datetime2" or "datetimeoffset" => SourceType.DateTime,
        _ => SourceType.Text
    };

    private static bool IsCompatibleSqlType(SourceType sourceType, string sqlType) => sourceType switch
    {
        SourceType.Guid or SourceType.Lookup => sqlType == "uniqueidentifier",
        SourceType.String or SourceType.Text or SourceType.MultiSelectChoice => sqlType is "nvarchar" or "nchar",
        SourceType.Boolean => sqlType == "bit",
        SourceType.Int32 or SourceType.Choice => sqlType == "int",
        SourceType.Int64 => sqlType == "bigint",
        SourceType.Decimal or SourceType.Money => sqlType is "decimal" or "numeric" or "money" or "smallmoney",
        SourceType.Double => sqlType is "float" or "real",
        SourceType.DateTime => sqlType is "date" or "datetime" or "datetime2" or "datetimeoffset",
        _ => false
    };
}
