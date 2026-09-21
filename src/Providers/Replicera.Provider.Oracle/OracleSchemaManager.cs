using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Core.Schema;

namespace Replicera.Provider.Oracle;

public sealed class OracleSchemaManager(string connectionString) : IDestinationSchemaManager
{
    public async Task<DestinationTable?> ReadTableAsync(TableDefinition source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = await GetCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var tableName = OracleIdentifier.Normalize(source.DestinationName);
        var managed = await IsManagedAsync(connection, schema, tableName, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, CHAR_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE
            FROM USER_TAB_COLUMNS
            WHERE TABLE_NAME = :table_name
            ORDER BY COLUMN_ID
            """;
        command.Parameters.Add("table_name", OracleDbType.Varchar2).Value = tableName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<DestinationColumn>();
        var sourceByName = source.Columns.ToDictionary(
            column => OracleIdentifier.Normalize(column.LogicalName),
            StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var sqlType = reader.GetString(1);
            var matched = sourceByName.GetValueOrDefault(name);
            var type = matched is not null && IsCompatibleSqlType(matched.SourceType, sqlType)
                ? matched.SourceType
                : InferSourceType(sqlType);
            columns.Add(new DestinationColumn(
                name,
                type,
                string.Equals(reader.GetString(5), "Y", StringComparison.Ordinal),
                reader.IsDBNull(2) || sqlType is "NCLOB" or "CLOB" ? null : Convert.ToInt32(reader.GetDecimal(2), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetDecimal(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetDecimal(4), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return columns.Count == 0 ? null : new DestinationTable(schema, tableName, columns, managed);
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

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureNoExternalDependenciesAsync(connection, source, plan, cancellationToken).ConfigureAwait(false);
        if (plan.Changes.Any(change => change.IsAutomatic && change.Kind is
                SchemaChangeKind.AddColumn or SchemaChangeKind.AddLookupTypeColumn or SchemaChangeKind.AddManagedColumn or SchemaChangeKind.RecreateTable))
        {
            await ResetCheckpointBeforeDdlAsync(connection, jobName, source.LogicalName, cancellationToken).ConfigureAwait(false);
        }

        foreach (var change in plan.Changes.Where(change => change.IsAutomatic))
        {
            var statements = change.Kind switch
            {
                SchemaChangeKind.CreateTable => [OracleDdlBuilder.BuildCreateTable(source)],
                SchemaChangeKind.RecreateTable => BuildRecreateTable(source),
                SchemaChangeKind.AddColumn => BuildAddColumn(source, change.ObjectName),
                SchemaChangeKind.AddLookupTypeColumn => [BuildAddLookupTypeColumn(source, change.ObjectName)],
                SchemaChangeKind.AddManagedColumn => [BuildAddManagedColumn(source, change.ObjectName)],
                SchemaChangeKind.RenameColumn => [BuildRenameColumn(source, change.ObjectName, change.NewObjectName!)],
                SchemaChangeKind.DropColumn => [BuildDropColumn(source, change.ObjectName)],
                SchemaChangeKind.ExpandColumn => [BuildAlterColumn(source, change.ObjectName)],
                _ => []
            };
            foreach (var sql in statements)
            {
                await ExecuteAsync(connection, null, sql, cancellationToken).ConfigureAwait(false);
            }
        }

        await using var transaction = connection.BeginTransaction();
        var schema = await GetCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var tableId = await EnsureOwnershipAsync(connection, transaction, schema, jobName, source, cancellationToken).ConfigureAwait(false);
        if (plan.Changes.Any(change => change.IsAutomatic && change.Kind is
                SchemaChangeKind.AddColumn or SchemaChangeKind.AddLookupTypeColumn or SchemaChangeKind.AddManagedColumn or SchemaChangeKind.RecreateTable))
        {
            await using var reset = connection.CreateCommand();
            reset.Transaction = transaction;
            reset.BindByName = true;
            reset.CommandText = "UPDATE REPLICERA_TABLES SET CHANGE_CHECKPOINT = NULL, STATUS = 'ResyncRequired' WHERE TABLE_ID = :table_id";
            reset.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
            _ = await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var change in plan.Changes)
        {
            await RecordSchemaChangeAsync(connection, transaction, tableId, change, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ResetCheckpointBeforeDdlAsync(
        OracleConnection connection,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.BindByName = true;
        command.CommandText = "UPDATE REPLICERA_TABLES SET CHANGE_CHECKPOINT = NULL, STATUS = 'ResyncRequired' WHERE REPLICATION_JOB_ID = :job_name AND DATAVERSE_LOGICAL_NAME = :logical_name";
        command.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
        command.Parameters.Add("logical_name", OracleDbType.NVarchar2).Value = logicalName;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReconcileMissingTableAsync(
        string jobName,
        string logicalName,
        SynchronizationMode mode,
        CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        Guid tableId;
        string destinationName;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.BindByName = true;
            lookup.CommandText = "SELECT TABLE_ID, DESTINATION_TABLE_NAME FROM REPLICERA_TABLES WHERE REPLICATION_JOB_ID = :job AND DATAVERSE_LOGICAL_NAME = :logical";
            lookup.Parameters.Add("job", OracleDbType.NVarchar2).Value = jobName;
            lookup.Parameters.Add("logical", OracleDbType.NVarchar2).Value = logicalName;
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            tableId = OracleValueConverter.ToGuid((byte[])reader.GetValue(0));
            destinationName = reader.GetString(1);
        }

        var drop = mode is SynchronizationMode.Complete or SynchronizationMode.Reload;
        if (drop)
        {
            await EnsureNoExternalDependenciesAsync(connection, destinationName, null, cancellationToken).ConfigureAwait(false);
            await using var exists = connection.CreateCommand();
            exists.BindByName = true;
            exists.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :table_name";
            exists.Parameters.Add("table_name", OracleDbType.Varchar2).Value = destinationName;
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                await ExecuteAsync(connection, null, $"DROP TABLE {OracleIdentifier.Quote(destinationName)}", cancellationToken).ConfigureAwait(false);
            }
        }

        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.BindByName = true;
            update.CommandText = "UPDATE REPLICERA_TABLES SET CHANGE_CHECKPOINT = NULL, STATUS = 'SourceRemoved' WHERE TABLE_ID = :table_id";
            update.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
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
        OracleConnection connection,
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
        OracleConnection connection,
        string tableName,
        string? columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = columnName is null
            ? """
                SELECT 'foreign key ' || CONSTRAINT_NAME FROM USER_CONSTRAINTS WHERE CONSTRAINT_TYPE = 'R' AND (TABLE_NAME = :table_name OR R_CONSTRAINT_NAME IN (SELECT CONSTRAINT_NAME FROM USER_CONSTRAINTS WHERE TABLE_NAME = :table_name))
                UNION ALL SELECT 'trigger ' || TRIGGER_NAME FROM USER_TRIGGERS WHERE TABLE_NAME = :table_name
                UNION ALL SELECT 'index ' || i.INDEX_NAME FROM USER_INDEXES i WHERE i.TABLE_NAME = :table_name AND NOT EXISTS (SELECT 1 FROM USER_CONSTRAINTS c WHERE c.INDEX_NAME = i.INDEX_NAME AND c.CONSTRAINT_TYPE IN ('P', 'U'))
                UNION ALL SELECT 'dependent object ' || NAME FROM USER_DEPENDENCIES WHERE REFERENCED_NAME = :table_name AND NAME <> :table_name
                UNION ALL SELECT 'permission for ' || GRANTEE FROM USER_TAB_PRIVS_MADE WHERE TABLE_NAME = :table_name
                """
            : """
                SELECT 'constraint ' || c.CONSTRAINT_NAME FROM USER_CONS_COLUMNS c WHERE c.TABLE_NAME = :table_name AND c.COLUMN_NAME = :column_name
                UNION ALL SELECT 'index ' || i.INDEX_NAME FROM USER_IND_COLUMNS i WHERE i.TABLE_NAME = :table_name AND i.COLUMN_NAME = :column_name
                UNION ALL SELECT 'dependent object ' || d.NAME FROM USER_DEPENDENCIES d WHERE d.REFERENCED_NAME = :table_name AND d.NAME <> :table_name
                """;
        command.Parameters.Add("table_name", OracleDbType.Varchar2).Value = OracleIdentifier.Normalize(tableName);
        if (columnName is not null)
        {
            command.Parameters.Add("column_name", OracleDbType.Varchar2).Value = OracleIdentifier.Normalize(columnName);
        }

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

    private static async Task<bool> IsManagedAsync(
        OracleConnection connection,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = 'REPLICERA_TABLES'";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = "SELECT COUNT(*) FROM REPLICERA_TABLES WHERE DESTINATION_SCHEMA = :schema AND DESTINATION_TABLE_NAME = :table_name";
        command.Parameters.Add("schema", OracleDbType.NVarchar2).Value = schema;
        command.Parameters.Add("table_name", OracleDbType.NVarchar2).Value = tableName;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<string> GetCurrentSchemaAsync(
        OracleConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL";
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Oracle did not return the current schema."));
    }

    private static async Task<Guid> EnsureOwnershipAsync(
        OracleConnection connection,
        OracleTransaction transaction,
        string schema,
        string jobName,
        TableDefinition table,
        CancellationToken cancellationToken)
    {
        var tableId = Guid.NewGuid();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.BindByName = true;
            command.CommandText = """
                MERGE INTO REPLICERA_TABLES target
                USING (SELECT :job_name AS REPLICATION_JOB_ID, :logical_name AS DATAVERSE_LOGICAL_NAME FROM DUAL) source
                ON (target.REPLICATION_JOB_ID = source.REPLICATION_JOB_ID AND target.DATAVERSE_LOGICAL_NAME = source.DATAVERSE_LOGICAL_NAME)
                WHEN NOT MATCHED THEN INSERT
                    (TABLE_ID, REPLICATION_JOB_ID, DATAVERSE_LOGICAL_NAME, DATAVERSE_ENTITY_SET_NAME,
                     DESTINATION_SCHEMA, DESTINATION_TABLE_NAME, PRIMARY_KEY, CHANGE_TRACKING_ENABLED, STATUS)
                VALUES
                    (:table_id, :job_name, :logical_name, :entity_set_name,
                     :destination_schema, :destination_name, :primary_key, 1, 'Uninitialized')
                """;
            command.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
            command.Parameters.Add("logical_name", OracleDbType.NVarchar2).Value = table.LogicalName;
            command.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
            command.Parameters.Add("entity_set_name", OracleDbType.NVarchar2).Value = table.EntitySetName;
            command.Parameters.Add("destination_schema", OracleDbType.NVarchar2).Value = schema;
            command.Parameters.Add("destination_name", OracleDbType.NVarchar2).Value = OracleIdentifier.Normalize(table.DestinationName);
            command.Parameters.Add("primary_key", OracleDbType.NVarchar2).Value = table.PrimaryKey.LogicalName;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.BindByName = true;
        lookup.CommandText = "SELECT TABLE_ID FROM REPLICERA_TABLES WHERE REPLICATION_JOB_ID = :job_name AND DATAVERSE_LOGICAL_NAME = :logical_name FOR UPDATE";
        lookup.Parameters.Add("job_name", OracleDbType.NVarchar2).Value = jobName;
        lookup.Parameters.Add("logical_name", OracleDbType.NVarchar2).Value = table.LogicalName;
        return OracleValueConverter.ToGuid((byte[])(await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Oracle did not return the managed table ID.")));
    }

    private static async Task RecordSchemaChangeAsync(
        OracleConnection connection,
        OracleTransaction transaction,
        Guid tableId,
        SchemaChange change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.BindByName = true;
        command.CommandText = """
            INSERT INTO REPLICERA_SCHEMA_HISTORY
                (SCHEMA_HISTORY_ID, TABLE_ID, DETECTED_UTC, APPLIED_UTC, CHANGE_KIND, OBJECT_NAME, DESCRIPTION, STATUS)
            VALUES
                (:history_id, :table_id, SYSTIMESTAMP,
                 CASE WHEN :applied = 1 THEN SYSTIMESTAMP ELSE NULL END,
                 :change_kind, :object_name, :description,
                 CASE WHEN :applied = 1 THEN 'Applied' ELSE 'Detected' END)
            """;
        command.Parameters.Add("history_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(Guid.NewGuid());
        command.Parameters.Add("table_id", OracleDbType.Raw, 16).Value = OracleValueConverter.ToBytes(tableId);
        command.Parameters.Add("applied", OracleDbType.Int16).Value = change.IsAutomatic ? 1 : 0;
        command.Parameters.Add("change_kind", OracleDbType.NVarchar2).Value = change.Kind.ToString();
        command.Parameters.Add("object_name", OracleDbType.NVarchar2).Value = change.ObjectName;
        command.Parameters.Add("description", OracleDbType.NVarchar2).Value = change.Description;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        OracleConnection connection,
        OracleTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // SQL is generated from normalized and quoted provider-owned identifiers.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<string> BuildAddColumn(TableDefinition table, string logicalName)
    {
        var column = table.Columns.Single(column => string.Equals(column.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        var tableName = OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName));
        var statements = new List<string>
        {
            $"ALTER TABLE {tableName} ADD ({OracleIdentifier.Quote(OracleIdentifier.Normalize(column.LogicalName))} {OracleTypeMapper.Map(column).Declaration} NULL)"
        };
        if (column.SourceType == SourceType.Lookup && column.LookupTargets.Count > 1)
        {
            statements.Add($"ALTER TABLE {tableName} ADD ({OracleIdentifier.Quote(OracleIdentifier.Normalize($"{column.LogicalName}_type"))} NVARCHAR2(128) NULL)");
        }

        return statements;
    }

    private static string BuildAddManagedColumn(TableDefinition table, string columnName) =>
        $"ALTER TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} ADD ({OracleIdentifier.Quote(OracleIdentifier.Normalize(columnName))} TIMESTAMP(7) WITH TIME ZONE NULL)";

    private static string BuildAddLookupTypeColumn(TableDefinition table, string logicalName) =>
        $"ALTER TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} ADD ({OracleIdentifier.Quote(OracleIdentifier.Normalize($"{logicalName}_type"))} NVARCHAR2(128) NULL)";

    private static List<string> BuildRecreateTable(TableDefinition table) =>
    [
        $"DROP TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))}",
        OracleDdlBuilder.BuildCreateTable(table)
    ];

    private static string BuildDropColumn(TableDefinition table, string destinationName)
    {
        var tableName = OracleIdentifier.Normalize(table.DestinationName);
        var columnName = OracleIdentifier.Normalize(destinationName);
        return $"ALTER TABLE {OracleIdentifier.Quote(tableName)} DROP COLUMN {OracleIdentifier.Quote(columnName)}";
    }

    private static string BuildRenameColumn(TableDefinition table, string oldName, string newName) =>
        $"ALTER TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} RENAME COLUMN {OracleIdentifier.Quote(OracleIdentifier.Normalize(oldName))} TO {OracleIdentifier.Quote(OracleIdentifier.Normalize(newName))}";

    private static string BuildAlterColumn(TableDefinition table, string logicalName)
    {
        var column = table.Columns.Single(column => string.Equals(column.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        return $"ALTER TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} MODIFY ({OracleIdentifier.Quote(OracleIdentifier.Normalize(column.LogicalName))} {OracleTypeMapper.Map(column).Declaration})";
    }

    private static SourceType InferSourceType(string sqlType) => sqlType switch
    {
        "RAW" => SourceType.Guid,
        "NUMBER" => SourceType.Decimal,
        "BINARY_DOUBLE" or "BINARY_FLOAT" => SourceType.Double,
        "DATE" => SourceType.DateTime,
        _ when sqlType.StartsWith("TIMESTAMP", StringComparison.Ordinal) => SourceType.DateTime,
        _ => SourceType.Text
    };

    private static bool IsCompatibleSqlType(SourceType sourceType, string sqlType) => sourceType switch
    {
        SourceType.Guid or SourceType.Lookup => sqlType == "RAW",
        SourceType.String or SourceType.Text or SourceType.MultiSelectChoice => sqlType is "NVARCHAR2" or "NCHAR" or "NCLOB" or "VARCHAR2" or "CLOB",
        SourceType.Boolean or SourceType.Int32 or SourceType.Int64 or SourceType.Choice or SourceType.Decimal or SourceType.Money => sqlType == "NUMBER",
        SourceType.Double => sqlType is "BINARY_DOUBLE" or "BINARY_FLOAT",
        SourceType.DateTime => sqlType == "DATE" || sqlType.StartsWith("TIMESTAMP", StringComparison.Ordinal),
        _ => false
    };
}
