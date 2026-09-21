using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Abstractions;
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
            if (name.EndsWith("_TYPE", StringComparison.Ordinal))
            {
                continue;
            }

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
        foreach (var change in plan.Changes.Where(change => change.IsAutomatic))
        {
            var statements = change.Kind switch
            {
                SchemaChangeKind.CreateTable => [OracleDdlBuilder.BuildCreateTable(source)],
                SchemaChangeKind.AddColumn => BuildAddColumn(source, change.ObjectName),
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
        foreach (var change in plan.Changes)
        {
            await RecordSchemaChangeAsync(connection, transaction, tableId, change, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        var nullable = column.IsNullable ? "NULL" : "NOT NULL";
        var statements = new List<string>
        {
            $"ALTER TABLE {tableName} ADD ({OracleIdentifier.Quote(OracleIdentifier.Normalize(column.LogicalName))} {OracleTypeMapper.Map(column).Declaration} {nullable})"
        };
        if (column.SourceType == SourceType.Lookup && column.LookupTargets.Count > 1)
        {
            statements.Add($"ALTER TABLE {tableName} ADD ({OracleIdentifier.Quote(OracleIdentifier.Normalize($"{column.LogicalName}_type"))} NVARCHAR2(128) {nullable})");
        }

        return statements;
    }

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
