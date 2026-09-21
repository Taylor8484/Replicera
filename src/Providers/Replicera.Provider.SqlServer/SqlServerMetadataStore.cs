using Microsoft.Data.SqlClient;

namespace Replicera.Provider.SqlServer;

public sealed class SqlServerMetadataStore(string connectionString)
{
    public const int CurrentSchemaVersion = 1;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandText = MigrationOne;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal const string MigrationOne = """
        IF SCHEMA_ID(N'replicera') IS NULL
            EXEC(N'CREATE SCHEMA [replicera]');

        IF OBJECT_ID(N'[replicera].[SchemaVersions]', N'U') IS NULL
        BEGIN
            CREATE TABLE [replicera].[SchemaVersions]
            (
                [Version] int NOT NULL CONSTRAINT [PK_replicera_SchemaVersions] PRIMARY KEY,
                [AppliedUtc] datetimeoffset(7) NOT NULL
            );
        END;

        IF NOT EXISTS (SELECT 1 FROM [replicera].[SchemaVersions] WHERE [Version] = 1)
        BEGIN
            CREATE TABLE [replicera].[Tables]
            (
                [TableId] uniqueidentifier NOT NULL CONSTRAINT [PK_replicera_Tables] PRIMARY KEY,
                [ReplicationJobId] nvarchar(128) NOT NULL,
                [DataverseLogicalName] nvarchar(128) NOT NULL,
                [DataverseEntitySetName] nvarchar(128) NOT NULL,
                [DestinationSchema] nvarchar(128) NOT NULL,
                [DestinationTableName] nvarchar(128) NOT NULL,
                [PrimaryKey] nvarchar(128) NOT NULL,
                [ChangeTrackingEnabled] bit NOT NULL,
                [ChangeCheckpoint] nvarchar(max) NULL,
                [MetadataVersion] nvarchar(256) NULL,
                [LastFullSyncUtc] datetimeoffset(7) NULL,
                [LastIncrementalSyncUtc] datetimeoffset(7) NULL,
                [LastSuccessfulSyncUtc] datetimeoffset(7) NULL,
                [Status] nvarchar(32) NOT NULL,
                CONSTRAINT [UQ_replicera_Tables_Job_Table] UNIQUE ([ReplicationJobId], [DataverseLogicalName]),
                CONSTRAINT [UQ_replicera_Tables_Destination] UNIQUE ([DestinationSchema], [DestinationTableName])
            );

            CREATE TABLE [replicera].[SyncRuns]
            (
                [SyncRunId] uniqueidentifier NOT NULL CONSTRAINT [PK_replicera_SyncRuns] PRIMARY KEY,
                [ReplicationJobId] nvarchar(128) NOT NULL,
                [TableId] uniqueidentifier NOT NULL,
                [SyncType] nvarchar(32) NOT NULL,
                [StartedUtc] datetimeoffset(7) NOT NULL,
                [CompletedUtc] datetimeoffset(7) NULL,
                [RecordsReceived] bigint NOT NULL CONSTRAINT [DF_replicera_SyncRuns_Received] DEFAULT 0,
                [RecordsInserted] bigint NOT NULL CONSTRAINT [DF_replicera_SyncRuns_Inserted] DEFAULT 0,
                [RecordsUpdated] bigint NOT NULL CONSTRAINT [DF_replicera_SyncRuns_Updated] DEFAULT 0,
                [RecordsDeleted] bigint NOT NULL CONSTRAINT [DF_replicera_SyncRuns_Deleted] DEFAULT 0,
                [PagesProcessed] bigint NOT NULL CONSTRAINT [DF_replicera_SyncRuns_Pages] DEFAULT 0,
                [Status] nvarchar(32) NOT NULL,
                [ErrorCode] nvarchar(64) NULL,
                [ErrorMessage] nvarchar(2048) NULL,
                CONSTRAINT [FK_replicera_SyncRuns_Tables] FOREIGN KEY ([TableId]) REFERENCES [replicera].[Tables] ([TableId])
            );

            CREATE TABLE [replicera].[SchemaHistory]
            (
                [SchemaHistoryId] uniqueidentifier NOT NULL CONSTRAINT [PK_replicera_SchemaHistory] PRIMARY KEY,
                [TableId] uniqueidentifier NOT NULL,
                [DetectedUtc] datetimeoffset(7) NOT NULL,
                [AppliedUtc] datetimeoffset(7) NULL,
                [ChangeKind] nvarchar(64) NOT NULL,
                [ObjectName] nvarchar(256) NOT NULL,
                [Description] nvarchar(2048) NOT NULL,
                [Status] nvarchar(32) NOT NULL,
                CONSTRAINT [FK_replicera_SchemaHistory_Tables] FOREIGN KEY ([TableId]) REFERENCES [replicera].[Tables] ([TableId])
            );

            INSERT INTO [replicera].[SchemaVersions] ([Version], [AppliedUtc]) VALUES (1, SYSUTCDATETIME());
        END;
        """;
}
