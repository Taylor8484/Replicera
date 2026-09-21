using Oracle.ManagedDataAccess.Client;

namespace Replicera.Provider.Oracle;

public sealed class OracleMetadataStore(string connectionString)
{
    public const int CurrentSchemaVersion = 1;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var statement in MigrationOne)
        {
            await ExecuteDdlAsync(connection, statement, cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            MERGE INTO REPLICERA_SCHEMA_VERSIONS target
            USING (SELECT 1 AS VERSION FROM DUAL) source
            ON (target.VERSION = source.VERSION)
            WHEN NOT MATCHED THEN INSERT (VERSION, APPLIED_UTC) VALUES (1, SYSTIMESTAMP)
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteDdlAsync(
        OracleConnection connection,
        string statement,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException exception) when (exception.Number == 955)
        {
            // Another run or a previous migration already created the object.
        }
    }

    internal static readonly string[] MigrationOne =
    [
        """
        CREATE TABLE REPLICERA_SCHEMA_VERSIONS
        (
            VERSION NUMBER(10) NOT NULL PRIMARY KEY,
            APPLIED_UTC TIMESTAMP(7) WITH TIME ZONE NOT NULL
        )
        """,
        """
        CREATE TABLE REPLICERA_TABLES
        (
            TABLE_ID RAW(16) NOT NULL PRIMARY KEY,
            REPLICATION_JOB_ID NVARCHAR2(128) NOT NULL,
            DATAVERSE_LOGICAL_NAME NVARCHAR2(128) NOT NULL,
            DATAVERSE_ENTITY_SET_NAME NVARCHAR2(128) NOT NULL,
            DESTINATION_SCHEMA NVARCHAR2(128) NOT NULL,
            DESTINATION_TABLE_NAME NVARCHAR2(128) NOT NULL,
            PRIMARY_KEY NVARCHAR2(128) NOT NULL,
            CHANGE_TRACKING_ENABLED NUMBER(1) NOT NULL,
            CHANGE_CHECKPOINT NCLOB NULL,
            METADATA_VERSION NVARCHAR2(256) NULL,
            LAST_FULL_SYNC_UTC TIMESTAMP(7) WITH TIME ZONE NULL,
            LAST_INCREMENTAL_SYNC_UTC TIMESTAMP(7) WITH TIME ZONE NULL,
            LAST_SUCCESSFUL_SYNC_UTC TIMESTAMP(7) WITH TIME ZONE NULL,
            STATUS NVARCHAR2(32) NOT NULL,
            CONSTRAINT UQ_REPLICERA_TABLES_JOB UNIQUE (REPLICATION_JOB_ID, DATAVERSE_LOGICAL_NAME),
            CONSTRAINT UQ_REPLICERA_TABLES_DEST UNIQUE (DESTINATION_SCHEMA, DESTINATION_TABLE_NAME)
        )
        """,
        """
        CREATE TABLE REPLICERA_SYNC_RUNS
        (
            SYNC_RUN_ID RAW(16) NOT NULL PRIMARY KEY,
            REPLICATION_JOB_ID NVARCHAR2(128) NOT NULL,
            TABLE_ID RAW(16) NOT NULL,
            SYNC_TYPE NVARCHAR2(32) NOT NULL,
            STARTED_UTC TIMESTAMP(7) WITH TIME ZONE NOT NULL,
            COMPLETED_UTC TIMESTAMP(7) WITH TIME ZONE NULL,
            RECORDS_RECEIVED NUMBER(19) DEFAULT 0 NOT NULL,
            RECORDS_INSERTED NUMBER(19) DEFAULT 0 NOT NULL,
            RECORDS_UPDATED NUMBER(19) DEFAULT 0 NOT NULL,
            RECORDS_DELETED NUMBER(19) DEFAULT 0 NOT NULL,
            PAGES_PROCESSED NUMBER(19) DEFAULT 0 NOT NULL,
            STATUS NVARCHAR2(32) NOT NULL,
            ERROR_CODE NVARCHAR2(64) NULL,
            ERROR_MESSAGE NVARCHAR2(2000) NULL,
            CONSTRAINT FK_REPLICERA_RUNS_TABLE FOREIGN KEY (TABLE_ID) REFERENCES REPLICERA_TABLES (TABLE_ID)
        )
        """,
        """
        CREATE TABLE REPLICERA_SCHEMA_HISTORY
        (
            SCHEMA_HISTORY_ID RAW(16) NOT NULL PRIMARY KEY,
            TABLE_ID RAW(16) NOT NULL,
            DETECTED_UTC TIMESTAMP(7) WITH TIME ZONE NOT NULL,
            APPLIED_UTC TIMESTAMP(7) WITH TIME ZONE NULL,
            CHANGE_KIND NVARCHAR2(64) NOT NULL,
            OBJECT_NAME NVARCHAR2(256) NOT NULL,
            DESCRIPTION NVARCHAR2(2000) NOT NULL,
            STATUS NVARCHAR2(32) NOT NULL,
            CONSTRAINT FK_REPLICERA_HISTORY_TABLE FOREIGN KEY (TABLE_ID) REFERENCES REPLICERA_TABLES (TABLE_ID)
        )
        """
    ];
}
