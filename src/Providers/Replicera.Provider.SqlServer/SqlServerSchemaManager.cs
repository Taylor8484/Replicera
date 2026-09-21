using Microsoft.Data.SqlClient;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;
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
        await EnsureNoExternalDependenciesAsync(connection, source, plan, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in plan.Changes.Where(change => change.IsAutomatic))
        {
            var sql = change.Kind switch
            {
                SchemaChangeKind.CreateTable => SqlServerDdlBuilder.BuildCreateTable(source),
                SchemaChangeKind.RecreateTable => BuildRecreateTable(source),
                SchemaChangeKind.AddColumn => BuildAddColumn(source, change.ObjectName),
                SchemaChangeKind.AddLookupTypeColumn => BuildAddLookupTypeColumn(source, change.ObjectName),
                SchemaChangeKind.AddManagedColumn => BuildAddManagedColumn(source, change.ObjectName),
                SchemaChangeKind.RenameColumn => BuildRenameColumn(source, change.ObjectName, change.NewObjectName!),
                SchemaChangeKind.DropColumn => BuildDropColumn(source, change.ObjectName),
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
        if (plan.Changes.Any(change => change.IsAutomatic && change.Kind is
                SchemaChangeKind.AddColumn or SchemaChangeKind.AddLookupTypeColumn or SchemaChangeKind.AddManagedColumn or SchemaChangeKind.RecreateTable))
        {
            await using var reset = connection.CreateCommand();
            reset.Transaction = transaction;
            reset.CommandText = "UPDATE [replicera].[Tables] SET [ChangeCheckpoint] = NULL, [Status] = N'ResyncRequired' WHERE [TableId] = @tableId;";
            _ = reset.Parameters.AddWithValue("@tableId", tableId);
            _ = await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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

    public async Task<bool> ReconcileMissingTableAsync(
        string jobName,
        string logicalName,
        SynchronizationMode mode,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT [TableId], [DestinationTableName] FROM [replicera].[Tables] WITH (UPDLOCK) WHERE [ReplicationJobId] = @job AND [DataverseLogicalName] = @logical;";
        _ = lookup.Parameters.AddWithValue("@job", jobName);
        _ = lookup.Parameters.AddWithValue("@logical", logicalName);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var tableId = reader.GetGuid(0);
        var destinationName = reader.GetString(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        var drop = mode is SynchronizationMode.Complete or SynchronizationMode.Reload;
        if (drop)
        {
            await EnsureNoExternalDependenciesAsync(connection, destinationName, null, cancellationToken, transaction).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
#pragma warning disable CA2100 // Destination name comes from normalized Replicera-owned metadata.
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].{SqlServerIdentifier.Quote(destinationName)};";
#pragma warning restore CA2100
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE [replicera].[Tables] SET [ChangeCheckpoint] = NULL, [Status] = N'SourceRemoved' WHERE [TableId] = @tableId;";
            _ = update.Parameters.AddWithValue("@tableId", tableId);
            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordSchemaChangeAsync(connection, transaction, tableId, new SchemaChange(
            SchemaChangeKind.DropTable,
            destinationName,
            $"Source table '{logicalName}' no longer exists; {(drop ? "drop" : "retain")} its destination table.",
            drop,
            false), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return drop;
    }

    private static async Task EnsureNoExternalDependenciesAsync(
        SqlConnection connection,
        TableDefinition source,
        SchemaPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Changes.Any(change => change.IsAutomatic && change.Kind == SchemaChangeKind.RecreateTable))
        {
            await EnsureNoExternalDependenciesAsync(connection, source.DestinationName, null, cancellationToken).ConfigureAwait(false);
        }

        foreach (var change in plan.Changes.Where(change => change.IsAutomatic && change.Kind == SchemaChangeKind.DropColumn))
        {
            await EnsureNoExternalDependenciesAsync(connection, source.DestinationName, change.ObjectName, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureNoExternalDependenciesAsync(
        SqlConnection connection,
        string tableName,
        string? columnName,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = columnName is null
            ? """
                DECLARE @objectId int = OBJECT_ID(N'[dbo].' + QUOTENAME(@table), N'U');
                SELECT N'foreign key ' + QUOTENAME([name]) FROM sys.foreign_keys WHERE [parent_object_id] = @objectId OR [referenced_object_id] = @objectId
                UNION ALL SELECT N'trigger ' + QUOTENAME([name]) FROM sys.triggers WHERE [parent_id] = @objectId AND [is_ms_shipped] = 0
                UNION ALL SELECT N'index ' + QUOTENAME([name]) FROM sys.indexes WHERE [object_id] = @objectId AND [index_id] > 0 AND [is_primary_key] = 0 AND [is_unique_constraint] = 0 AND [name] IS NOT NULL
                UNION ALL SELECT N'dependent object ' + QUOTENAME(OBJECT_SCHEMA_NAME([referencing_id])) + N'.' + QUOTENAME(OBJECT_NAME([referencing_id])) FROM sys.sql_expression_dependencies WHERE [referenced_id] = @objectId AND [referencing_id] <> @objectId
                UNION ALL SELECT N'permission for ' + QUOTENAME(USER_NAME([grantee_principal_id])) FROM sys.database_permissions WHERE [class] = 1 AND [major_id] = @objectId AND [minor_id] = 0;
                """
            : """
                DECLARE @objectId int = OBJECT_ID(N'[dbo].' + QUOTENAME(@table), N'U');
                DECLARE @columnId int = COLUMNPROPERTY(@objectId, @column, 'ColumnId');
                SELECT N'index ' + QUOTENAME(i.[name]) FROM sys.indexes i INNER JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] WHERE i.[object_id] = @objectId AND ic.[column_id] = @columnId
                UNION ALL SELECT N'foreign key ' + QUOTENAME(fk.[name]) FROM sys.foreign_keys fk INNER JOIN sys.foreign_key_columns fkc ON fkc.[constraint_object_id] = fk.[object_id] WHERE (fkc.[parent_object_id] = @objectId AND fkc.[parent_column_id] = @columnId) OR (fkc.[referenced_object_id] = @objectId AND fkc.[referenced_column_id] = @columnId)
                UNION ALL SELECT N'default constraint ' + QUOTENAME([name]) FROM sys.default_constraints WHERE [parent_object_id] = @objectId AND [parent_column_id] = @columnId
                UNION ALL SELECT N'dependent object ' + QUOTENAME(OBJECT_SCHEMA_NAME([referencing_id])) + N'.' + QUOTENAME(OBJECT_NAME([referencing_id])) FROM sys.sql_expression_dependencies WHERE [referenced_id] = @objectId AND [referenced_minor_id] = @columnId;
                """;
        _ = command.Parameters.AddWithValue("@table", SqlServerIdentifier.Normalize(tableName));
        _ = command.Parameters.AddWithValue("@column", columnName is null ? DBNull.Value : SqlServerIdentifier.Normalize(columnName));
        var dependencies = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            dependencies.Add(reader.GetString(0));
        }

        if (dependencies.Count > 0)
        {
            throw new RepliceraException(
                ErrorCategory.SchemaConflict,
                $"Cannot drop {(columnName is null ? $"table '{tableName}'" : $"column '{tableName}.{columnName}'")} because it has external dependencies: {string.Join(", ", dependencies.Distinct(StringComparer.OrdinalIgnoreCase))}.");
        }
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
        var tableName = $"[dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))}";
        var sql = $"ALTER TABLE {tableName} ADD {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(column.LogicalName))} {type} NULL;";
        if (column.SourceType == SourceType.Lookup && column.LookupTargets.Count > 1)
        {
            sql += $"{Environment.NewLine}ALTER TABLE {tableName} ADD {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize($"{column.LogicalName}_type"))} nvarchar(128) NULL;";
        }

        return sql;
    }

    private static string BuildAddManagedColumn(TableDefinition table, string columnName) =>
        $"ALTER TABLE [dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))} ADD {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(columnName))} datetimeoffset(7) NULL;";

    private static string BuildAddLookupTypeColumn(TableDefinition table, string logicalName) =>
        $"ALTER TABLE [dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))} ADD {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize($"{logicalName}_type"))} nvarchar(128) NULL;";

    private static string BuildRecreateTable(TableDefinition table) =>
        $"DROP TABLE [dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))};{Environment.NewLine}{SqlServerDdlBuilder.BuildCreateTable(table)}";

    private static string BuildDropColumn(TableDefinition table, string destinationName)
    {
        var tableName = SqlServerIdentifier.Normalize(table.DestinationName);
        var columnName = SqlServerIdentifier.Normalize(destinationName);
        return $"ALTER TABLE [dbo].{SqlServerIdentifier.Quote(tableName)} DROP COLUMN {SqlServerIdentifier.Quote(columnName)};";
    }

    private static string BuildRenameColumn(TableDefinition table, string oldName, string newName) =>
        $"EXEC sys.sp_rename N'[dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))}.{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(oldName))}', N'{SqlServerIdentifier.Normalize(newName).Replace("'", "''", StringComparison.Ordinal)}', N'COLUMN';";

    private static string BuildAlterColumn(TableDefinition table, string logicalName)
    {
        var column = table.Columns.Single(column => string.Equals(column.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        var type = SqlServerTypeMapper.Map(column).Declaration;
        return $"ALTER TABLE [dbo].{SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(table.DestinationName))} ALTER COLUMN {SqlServerIdentifier.Quote(SqlServerIdentifier.Normalize(column.LogicalName))} {type} NULL;";
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
