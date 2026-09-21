using Npgsql;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Core.Schema;

namespace Replicera.Provider.PostgreSql;

public sealed class PostgreSqlSchemaManager(string connectionString) : IDestinationSchemaManager
{
    public async Task<DestinationTable?> ReadTableAsync(TableDefinition source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var metadataCommand = new NpgsqlCommand("SELECT to_regclass('replicera.tables') IS NOT NULL;", connection);
        var hasMetadata = (bool)(await metadataCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
        var managedExpression = hasMetadata
            ? "EXISTS (SELECT 1 FROM replicera.tables AS managed WHERE managed.destination_schema = c.table_schema AND managed.destination_table_name = c.table_name)"
            : "FALSE";
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The only interpolation selects a fixed provider-owned expression.
        command.CommandText = $"""
            SELECT c.column_name, c.data_type, c.character_maximum_length,
                   c.numeric_precision, c.numeric_scale, c.is_nullable = 'YES',
                   {managedExpression}
            FROM information_schema.columns AS c
            WHERE c.table_schema = 'public' AND c.table_name = @table
            ORDER BY c.ordinal_position;
            """;
#pragma warning restore CA2100
        _ = command.Parameters.AddWithValue("table", PostgreSqlIdentifier.Normalize(source.DestinationName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<DestinationColumn>();
        var managed = false;
        var sourceByName = source.Columns.ToDictionary(
            column => PostgreSqlIdentifier.Normalize(column.LogicalName),
            StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            managed = reader.GetBoolean(6);
            var sqlType = reader.GetString(1);
            var matched = sourceByName.GetValueOrDefault(name);
            var type = matched is not null && IsCompatibleSqlType(matched.SourceType, sqlType)
                ? matched.SourceType
                : InferSourceType(sqlType);
            columns.Add(new DestinationColumn(
                name,
                type,
                reader.GetBoolean(5),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        return columns.Count == 0
            ? null
            : new DestinationTable("public", PostgreSqlIdentifier.Normalize(source.DestinationName), columns, managed);
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureNoExternalDependenciesAsync(connection, source, plan, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in plan.Changes.Where(change => change.IsAutomatic))
        {
            var sql = change.Kind switch
            {
                SchemaChangeKind.CreateTable => PostgreSqlDdlBuilder.BuildCreateTable(source),
                SchemaChangeKind.RecreateTable => BuildRecreateTable(source),
                SchemaChangeKind.AddColumn => BuildAddColumn(source, change.ObjectName),
                SchemaChangeKind.AddLookupTypeColumn => BuildAddLookupTypeColumn(source, change.ObjectName),
                SchemaChangeKind.AddManagedColumn => BuildAddManagedColumn(source, change.ObjectName),
                SchemaChangeKind.RenameColumn => BuildRenameColumn(source, change.ObjectName, change.NewObjectName!),
                SchemaChangeKind.DropColumn => BuildDropColumn(source, change.ObjectName),
                SchemaChangeKind.ExpandColumn => BuildAlterColumn(source, change.ObjectName),
                _ => null
            };
            if (sql is not null)
            {
                await ExecuteAsync(connection, transaction, sql, cancellationToken).ConfigureAwait(false);
            }
        }

        var tableId = await EnsureOwnershipAsync(connection, transaction, jobName, source, cancellationToken).ConfigureAwait(false);
        if (plan.Changes.Any(change => change.IsAutomatic && change.Kind is
                SchemaChangeKind.AddColumn or SchemaChangeKind.AddLookupTypeColumn or SchemaChangeKind.AddManagedColumn or SchemaChangeKind.RecreateTable))
        {
            await using var reset = new NpgsqlCommand(
                "UPDATE replicera.tables SET change_checkpoint = NULL, status = 'ResyncRequired' WHERE table_id = @table_id;",
                connection,
                transaction);
            reset.Parameters.AddWithValue("table_id", tableId);
            _ = await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var change in plan.Changes)
        {
            await RecordSchemaChangeAsync(connection, transaction, tableId, change, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReconcileMissingTableAsync(
        string jobName,
        string logicalName,
        SynchronizationMode mode,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var lookup = new NpgsqlCommand(
            "SELECT table_id, destination_table_name FROM replicera.tables WHERE replication_job_id = @job AND dataverse_logical_name = @logical FOR UPDATE;",
            connection,
            transaction);
        lookup.Parameters.AddWithValue("job", jobName);
        lookup.Parameters.AddWithValue("logical", logicalName);
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
            await ExecuteAsync(connection, transaction, $"DROP TABLE IF EXISTS {PostgreSqlIdentifier.Qualified("public", destinationName)};", cancellationToken).ConfigureAwait(false);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE replicera.tables SET change_checkpoint = NULL, status = 'SourceRemoved' WHERE table_id = @table_id;",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("table_id", tableId);
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
        NpgsqlConnection connection,
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
        NpgsqlConnection connection,
        string tableName,
        string? columnName,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = columnName is null
            ? """
                WITH target AS (SELECT to_regclass('public.' || quote_ident(@table)) AS oid)
                SELECT 'foreign key ' || quote_ident(c.conname) FROM pg_constraint c, target t WHERE c.contype = 'f' AND (c.conrelid = t.oid OR c.confrelid = t.oid)
                UNION ALL SELECT 'trigger ' || quote_ident(g.tgname) FROM pg_trigger g, target t WHERE g.tgrelid = t.oid AND NOT g.tgisinternal
                UNION ALL SELECT 'index ' || quote_ident(i.relname) FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid, target t WHERE x.indrelid = t.oid AND NOT x.indisprimary AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = x.indexrelid)
                UNION ALL SELECT 'dependent view ' || quote_ident(n.nspname) || '.' || quote_ident(v.relname) FROM pg_depend d JOIN pg_rewrite r ON r.oid = d.objid JOIN pg_class v ON v.oid = r.ev_class JOIN pg_namespace n ON n.oid = v.relnamespace, target t WHERE d.refobjid = t.oid AND v.oid <> t.oid
                UNION ALL SELECT 'permission for ' || quote_ident(grantee) FROM information_schema.role_table_grants WHERE table_schema = 'public' AND table_name = @table AND grantee <> current_user;
                """
            : """
                WITH target AS (
                    SELECT c.oid, a.attnum
                    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_attribute a ON a.attrelid = c.oid
                    WHERE n.nspname = 'public' AND c.relname = @table AND a.attname = @column)
                SELECT 'index ' || quote_ident(i.relname) FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid, target t WHERE x.indrelid = t.oid AND t.attnum = ANY(x.indkey)
                UNION ALL SELECT 'constraint ' || quote_ident(c.conname) FROM pg_constraint c, target t WHERE (c.conrelid = t.oid AND t.attnum = ANY(c.conkey)) OR (c.confrelid = t.oid AND t.attnum = ANY(c.confkey))
                UNION ALL SELECT 'dependent object ' || d.objid::regclass::text FROM pg_depend d, target t WHERE d.refobjid = t.oid AND d.refobjsubid = t.attnum AND d.deptype <> 'i';
                """;
        command.Parameters.AddWithValue("table", PostgreSqlIdentifier.Normalize(tableName));
        if (columnName is not null)
        {
            command.Parameters.AddWithValue("column", PostgreSqlIdentifier.Normalize(columnName));
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

    private static async Task<Guid> EnsureOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string jobName,
        TableDefinition table,
        CancellationToken cancellationToken)
    {
        var tableId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO replicera.tables
                (table_id, replication_job_id, dataverse_logical_name, dataverse_entity_set_name,
                 destination_schema, destination_table_name, primary_key, change_tracking_enabled, status)
            VALUES
                (@table_id, @job_name, @logical_name, @entity_set_name,
                 'public', @destination_name, @primary_key, TRUE, 'Uninitialized')
            ON CONFLICT (replication_job_id, dataverse_logical_name) DO NOTHING;

            SELECT table_id
            FROM replicera.tables
            WHERE replication_job_id = @job_name AND dataverse_logical_name = @logical_name
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("table_id", tableId);
        command.Parameters.AddWithValue("job_name", jobName);
        command.Parameters.AddWithValue("logical_name", table.LogicalName);
        command.Parameters.AddWithValue("entity_set_name", table.EntitySetName);
        command.Parameters.AddWithValue("destination_name", PostgreSqlIdentifier.Normalize(table.DestinationName));
        command.Parameters.AddWithValue("primary_key", table.PrimaryKey.LogicalName);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL did not return the managed table ID."));
    }

    private static async Task RecordSchemaChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tableId,
        SchemaChange change,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO replicera.schema_history
                (schema_history_id, table_id, detected_utc, applied_utc, change_kind, object_name, description, status)
            VALUES
                (@history_id, @table_id, CURRENT_TIMESTAMP,
                 CASE WHEN @applied THEN CURRENT_TIMESTAMP ELSE NULL END,
                 @change_kind, @object_name, @description,
                 CASE WHEN @applied THEN 'Applied' ELSE 'Detected' END);
            """, connection, transaction);
        command.Parameters.AddWithValue("history_id", Guid.NewGuid());
        command.Parameters.AddWithValue("table_id", tableId);
        command.Parameters.AddWithValue("applied", change.IsAutomatic);
        command.Parameters.AddWithValue("change_kind", change.Kind.ToString());
        command.Parameters.AddWithValue("object_name", change.ObjectName);
        command.Parameters.AddWithValue("description", change.Description);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // SQL is generated from normalized and quoted provider-owned identifiers.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAddColumn(TableDefinition table, string logicalName)
    {
        var column = table.Columns.Single(column => string.Equals(column.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        var type = PostgreSqlTypeMapper.Map(column).Declaration;
        var tableName = PostgreSqlIdentifier.Qualified("public", table.DestinationName);
        var sql = $"ALTER TABLE {tableName} ADD COLUMN {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(column.LogicalName))} {type} NULL;";
        if (column.SourceType == SourceType.Lookup && column.LookupTargets.Count > 1)
        {
            sql += $"{Environment.NewLine}ALTER TABLE {tableName} ADD COLUMN {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize($"{column.LogicalName}_type"))} character varying(128) NULL;";
        }

        return sql;
    }

    private static string BuildAddManagedColumn(TableDefinition table, string columnName) =>
        $"ALTER TABLE {PostgreSqlIdentifier.Qualified("public", table.DestinationName)} ADD COLUMN {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(columnName))} timestamp with time zone NULL;";

    private static string BuildAddLookupTypeColumn(TableDefinition table, string logicalName) =>
        $"ALTER TABLE {PostgreSqlIdentifier.Qualified("public", table.DestinationName)} ADD COLUMN {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize($"{logicalName}_type"))} character varying(128) NULL;";

    private static string BuildRecreateTable(TableDefinition table) =>
        $"DROP TABLE {PostgreSqlIdentifier.Qualified("public", table.DestinationName)};{Environment.NewLine}{PostgreSqlDdlBuilder.BuildCreateTable(table)}";

    private static string BuildDropColumn(TableDefinition table, string destinationName)
    {
        var tableName = PostgreSqlIdentifier.Qualified("public", table.DestinationName);
        var columnName = PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(destinationName));
        return $"ALTER TABLE {tableName} DROP COLUMN {columnName};";
    }

    private static string BuildRenameColumn(TableDefinition table, string oldName, string newName) =>
        $"ALTER TABLE {PostgreSqlIdentifier.Qualified("public", table.DestinationName)} RENAME COLUMN {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(oldName))} TO {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(newName))};";

    private static string BuildAlterColumn(TableDefinition table, string logicalName)
    {
        var column = table.Columns.Single(column => string.Equals(column.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        return $"ALTER TABLE {PostgreSqlIdentifier.Qualified("public", table.DestinationName)} ALTER COLUMN {PostgreSqlIdentifier.Quote(PostgreSqlIdentifier.Normalize(column.LogicalName))} TYPE {PostgreSqlTypeMapper.Map(column).Declaration};";
    }

    private static SourceType InferSourceType(string sqlType) => sqlType switch
    {
        "uuid" => SourceType.Guid,
        "boolean" => SourceType.Boolean,
        "integer" => SourceType.Int32,
        "bigint" => SourceType.Int64,
        "numeric" or "decimal" => SourceType.Decimal,
        "double precision" or "real" => SourceType.Double,
        "date" or "timestamp with time zone" or "timestamp without time zone" => SourceType.DateTime,
        _ => SourceType.Text
    };

    private static bool IsCompatibleSqlType(SourceType sourceType, string sqlType) => sourceType switch
    {
        SourceType.Guid or SourceType.Lookup => sqlType == "uuid",
        SourceType.String or SourceType.Text or SourceType.MultiSelectChoice => sqlType is "text" or "character varying" or "character",
        SourceType.Boolean => sqlType == "boolean",
        SourceType.Int32 or SourceType.Choice => sqlType == "integer",
        SourceType.Int64 => sqlType == "bigint",
        SourceType.Decimal or SourceType.Money => sqlType is "numeric" or "decimal",
        SourceType.Double => sqlType is "double precision" or "real",
        SourceType.DateTime => sqlType is "date" or "timestamp with time zone" or "timestamp without time zone",
        _ => false
    };
}
