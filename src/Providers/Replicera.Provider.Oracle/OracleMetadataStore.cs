using Oracle.ManagedDataAccess.Client;

namespace Replicera.Provider.Oracle;

public sealed class OracleMetadataStore(string connectionString)
{
    public const int CurrentSchemaVersion = 2;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false) >= CurrentSchemaVersion)
        {
            return;
        }

        // The migration DDL needs exclusive table locks, so concurrent runs are serialized with a
        // session lock instead of failing with ORA-00054.
        await RequestMetadataLockAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!await TryReleaseMetadataLockAsync(connection).ConfigureAwait(false))
            {
                // The session lock would otherwise stay with the pooled session and block other runs.
                OracleConnection.ClearPool(connection);
            }
        }
    }

    private static async Task ApplyMigrationsAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        foreach (var statement in MigrationOne)
        {
            await ExecuteDdlAsync(connection, statement, cancellationToken).ConfigureAwait(false);
        }

        foreach (var statement in MigrationTwo)
        {
            await ExecuteDdlAsync(connection, statement, cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            MERGE INTO REPLICERA_SCHEMA_VERSIONS target
            USING (SELECT 1 AS VERSION FROM DUAL UNION ALL SELECT 2 AS VERSION FROM DUAL) source
            ON (target.VERSION = source.VERSION)
            WHEN NOT MATCHED THEN INSERT (VERSION, APPLIED_UTC) VALUES (source.VERSION, SYSTIMESTAMP)
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = 'REPLICERA_SCHEMA_VERSIONS'";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return 0;
        }

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT NVL(MAX(VERSION), 0) FROM REPLICERA_SCHEMA_VERSIONS";
        return Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string MetadataLockName = "replicera:metadata";

    private static async Task RequestMetadataLockAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            DECLARE
                result INTEGER;
            BEGIN
                result := DBMS_LOCK.REQUEST(DBMS_UTILITY.GET_HASH_VALUE(:lock_name, 0, 1073741823), 6, 60, FALSE);
                IF result NOT IN (0, 4) THEN
                    RAISE_APPLICATION_ERROR(-20001, 'Could not acquire the Replicera metadata lock (DBMS_LOCK result ' || result || ').');
                END IF;
            END;
            """;
        command.Parameters.Add("lock_name", OracleDbType.NVarchar2).Value = MetadataLockName;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // DBMS_LOCK.RELEASE returns 0 when released and 4 when the session did not hold the lock.
    private static async Task<bool> TryReleaseMetadataLockAsync(OracleConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandText = "BEGIN :result := DBMS_LOCK.RELEASE(DBMS_UTILITY.GET_HASH_VALUE(:lock_name, 0, 1073741823)); END;";
            var result = command.Parameters.Add("result", OracleDbType.Int32);
            result.Direction = System.Data.ParameterDirection.Output;
            command.Parameters.Add("lock_name", OracleDbType.NVarchar2).Value = MetadataLockName;
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            var code = result.Value is global::Oracle.ManagedDataAccess.Types.OracleDecimal oracleResult
                ? oracleResult.ToInt32()
                : Convert.ToInt32(result.Value, System.Globalization.CultureInfo.InvariantCulture);
            return code is 0 or 4;
        }
        catch (Exception exception) when (exception is OracleException or InvalidOperationException)
        {
            return false;
        }
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
        catch (OracleException exception) when (exception.Number is 955 or 1430)
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

    internal static readonly string[] MigrationTwo =
    [
        "ALTER TABLE REPLICERA_TABLES ADD (LAST_SYNC_MODE NVARCHAR2(32) NULL)"
    ];
}
