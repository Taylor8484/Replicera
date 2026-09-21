using Npgsql;

namespace Replicera.Provider.PostgreSql;

public sealed class PostgreSqlMetadataStore(string connectionString)
{
    public const int CurrentSchemaVersion = 1;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(MigrationOne, connection, transaction);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal const string MigrationOne = """
        CREATE SCHEMA IF NOT EXISTS replicera;

        CREATE TABLE IF NOT EXISTS replicera.schema_versions
        (
            version integer PRIMARY KEY,
            applied_utc timestamp with time zone NOT NULL
        );

        CREATE TABLE IF NOT EXISTS replicera.tables
        (
            table_id uuid PRIMARY KEY,
            replication_job_id character varying(128) NOT NULL,
            dataverse_logical_name character varying(128) NOT NULL,
            dataverse_entity_set_name character varying(128) NOT NULL,
            destination_schema character varying(128) NOT NULL,
            destination_table_name character varying(128) NOT NULL,
            primary_key character varying(128) NOT NULL,
            change_tracking_enabled boolean NOT NULL,
            change_checkpoint text NULL,
            metadata_version character varying(256) NULL,
            last_full_sync_utc timestamp with time zone NULL,
            last_incremental_sync_utc timestamp with time zone NULL,
            last_successful_sync_utc timestamp with time zone NULL,
            status character varying(32) NOT NULL,
            CONSTRAINT uq_replicera_tables_job_table UNIQUE (replication_job_id, dataverse_logical_name),
            CONSTRAINT uq_replicera_tables_destination UNIQUE (destination_schema, destination_table_name)
        );

        CREATE TABLE IF NOT EXISTS replicera.sync_runs
        (
            sync_run_id uuid PRIMARY KEY,
            replication_job_id character varying(128) NOT NULL,
            table_id uuid NOT NULL REFERENCES replicera.tables (table_id),
            sync_type character varying(32) NOT NULL,
            started_utc timestamp with time zone NOT NULL,
            completed_utc timestamp with time zone NULL,
            records_received bigint NOT NULL DEFAULT 0,
            records_inserted bigint NOT NULL DEFAULT 0,
            records_updated bigint NOT NULL DEFAULT 0,
            records_deleted bigint NOT NULL DEFAULT 0,
            pages_processed bigint NOT NULL DEFAULT 0,
            status character varying(32) NOT NULL,
            error_code character varying(64) NULL,
            error_message character varying(2048) NULL
        );

        CREATE TABLE IF NOT EXISTS replicera.schema_history
        (
            schema_history_id uuid PRIMARY KEY,
            table_id uuid NOT NULL REFERENCES replicera.tables (table_id),
            detected_utc timestamp with time zone NOT NULL,
            applied_utc timestamp with time zone NULL,
            change_kind character varying(64) NOT NULL,
            object_name character varying(256) NOT NULL,
            description character varying(2048) NOT NULL,
            status character varying(32) NOT NULL
        );

        INSERT INTO replicera.schema_versions (version, applied_utc)
        VALUES (1, CURRENT_TIMESTAMP)
        ON CONFLICT (version) DO NOTHING;
        """;
}
