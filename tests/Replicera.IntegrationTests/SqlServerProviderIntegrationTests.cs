using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Replicera.Cli;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Core.Replication;
using Replicera.Core.Schema;
using Replicera.Provider.SqlServer;

namespace Replicera.IntegrationTests;

public sealed class SqlServerProviderIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "REPLICERA_SQL_TEST_CONNECTION_STRING";
    private static readonly CancellationToken TestCancellationToken = CancellationToken.None;

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task TableLockAndDependencyPreflight_ProtectSchemaLifecycle()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var provider = new SqlServerProvider();
        await using (var tableLock = await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken))
        {
            await Assert.ThrowsAsync<Replicera.Core.Errors.SynchronizationAlreadyRunningException>(async () =>
                await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken));
        }

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE VIEW [dbo].[account_view] AS SELECT [accountid] FROM [dbo].[account];";
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var current = await schema.ReadTableAsync(table, TestCancellationToken);
        var plan = SchemaPlanner.Plan(table, current, new SchemaPolicy(), SynchronizationMode.Reload);
        var error = await Assert.ThrowsAsync<RepliceraException>(() =>
            schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken));
        Assert.Equal(ErrorCategory.SchemaConflict, error.Category);
        Assert.Contains("account_view", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task InitialAndIncrementalSync_CommitRowsMetricsAndCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();

        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            var result = await session.ApplyPageAsync(
                Page(
                    Upsert(firstId, "First", 1),
                    Upsert(secondId, "Second", 2)),
                TestCancellationToken);

            Assert.Equal(new(2, 0, 0), result);
            await session.CommitAsync("checkpoint-1", new(1, 2, 2, 0, 0), TestCancellationToken);
        }

        await using (var session = await writer.BeginIncrementalSyncAsync(
            "integration",
            table,
            "checkpoint-1",
            TestCancellationToken))
        {
            var result = await session.ApplyPageAsync(
                Page(
                    Upsert(firstId, "First updated", 3),
                    Delete(secondId),
                    Upsert(thirdId, "Third", 4)),
                TestCancellationToken);

            Assert.Equal(new(1, 1, 1), result);
            await session.CommitAsync("checkpoint-2", new(1, 3, 1, 1, 1), TestCancellationToken);
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [accountid], [name], [statuscode]
            FROM [dbo].[account]
            ORDER BY [name];

            SELECT [ChangeCheckpoint], [Status]
            FROM [replicera].[Tables]
            WHERE [ReplicationJobId] = N'integration' AND [DataverseLogicalName] = N'account';

            SELECT [RecordsInserted], [RecordsUpdated], [RecordsDeleted], [PagesProcessed], [Status]
            FROM [replicera].[SyncRuns]
            WHERE [SyncType] = N'Incremental';

            SELECT COUNT_BIG(*)
            FROM sys.tables
            WHERE [name] LIKE N'replicera_stage_%';
            """;
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(firstId, reader.GetGuid(0));
        Assert.Equal("First updated", reader.GetString(1));
        Assert.Equal(3, reader.GetInt32(2));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(thirdId, reader.GetGuid(0));
        Assert.False(await reader.ReadAsync(TestCancellationToken));

        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("checkpoint-2", reader.GetString(0));
        Assert.Equal("Healthy", reader.GetString(1));

        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal("Succeeded", reader.GetString(4));

        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(0, reader.GetInt64(0));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task DisposedSession_RollsBackRowsAndCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        var id = Guid.NewGuid();
        await using (var initial = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await initial.ApplyPageAsync(Page(Upsert(id, "Original", 1)), TestCancellationToken);
            await initial.CommitAsync("stable", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using (var interrupted = await writer.BeginIncrementalSyncAsync(
            "integration",
            table,
            "stable",
            TestCancellationToken))
        {
            _ = await interrupted.ApplyPageAsync(Page(Upsert(id, "Uncommitted", 2)), TestCancellationToken);
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [name] FROM [dbo].[account] WHERE [accountid] = @id;
            SELECT [ChangeCheckpoint] FROM [replicera].[Tables] WHERE [DataverseLogicalName] = N'account';
            """;
        _ = command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Original", reader.GetString(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("stable", reader.GetString(0));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task SchemaManager_ReadsUnmanagedTableWithoutMetadataSchema()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE [dbo].[account]
                (
                    [accountid] uniqueidentifier NOT NULL PRIMARY KEY,
                    [name] nvarchar(200) NULL,
                    [statuscode] int NULL
                );
                """;
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var destination = await new SqlServerSchemaManager(database.ConnectionString)
            .ReadTableAsync(AccountsTable(), TestCancellationToken);

        Assert.NotNull(destination);
        Assert.False(destination.IsManaged);
        var plan = SchemaPlanner.Plan(AccountsTable(), destination, new SchemaPolicy());
        Assert.Equal(SchemaChangeKind.OwnershipConflict, Assert.Single(plan.Changes).Kind);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task SchemaManager_AddsNullableColumnAndWidensText()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var initial = AccountsTable(nameLength: 20);
        await PrepareTableAsync(database.ConnectionString, initial);
        await using (var checkpointConnection = new SqlConnection(database.ConnectionString))
        {
            await checkpointConnection.OpenAsync(TestCancellationToken);
            await using var checkpoint = checkpointConnection.CreateCommand();
            checkpoint.CommandText = "UPDATE [replicera].[Tables] SET [ChangeCheckpoint] = N'old-checkpoint' WHERE [DataverseLogicalName] = N'account';";
            _ = await checkpoint.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var expanded = AccountsTable(nameLength: 400, includeDescription: true);
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var current = await schema.ReadTableAsync(expanded, TestCancellationToken);
        var plan = SchemaPlanner.Plan(expanded, current, new SchemaPolicy());

        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.ExpandColumn);
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.AddColumn);
        await schema.ApplySchemaPlanAsync("integration", expanded, plan, TestCancellationToken);

        var applied = await schema.ReadTableAsync(expanded, TestCancellationToken);
        Assert.NotNull(applied);
        Assert.Equal(400, applied.Columns.Single(column => column.Name == "name").MaxLength);
        Assert.Contains(applied.Columns, column => column.Name == "description" && column.IsNullable);
        var resetState = await new SqlServerReplicationStateStore(database.ConnectionString)
            .GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.NotNull(resetState);
        Assert.Null(resetState.DataCheckpoint);
        Assert.Equal(TableState.ResyncRequired, resetState.State);

        var contracted = AccountsTable(nameLength: 400);
        var dropPlan = SchemaPlanner.Plan(contracted, applied, new SchemaPolicy());
        Assert.Contains(dropPlan.Changes, change => change.Kind == SchemaChangeKind.DropColumn);
        await schema.ApplySchemaPlanAsync("integration", contracted, dropPlan, TestCancellationToken);
        var contractedDestination = await schema.ReadTableAsync(contracted, TestCancellationToken);
        Assert.NotNull(contractedDestination);
        Assert.DoesNotContain(contractedDestination.Columns, column => column.Name == "description");

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM [replicera].[SchemaHistory];";
        Assert.Equal(5, Convert.ToInt64(await command.ExecuteScalarAsync(TestCancellationToken), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task RequiredSourceColumn_IsRelaxedAndAcceptsNullValues()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable(includeDescription: true);
        await PrepareTableAsync(database.ConnectionString, table);
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = "ALTER TABLE [dbo].[account] ALTER COLUMN [description] nvarchar(1000) NOT NULL;";
            _ = await legacy.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync(table, TestCancellationToken), new SchemaPolicy());
        var relax = Assert.Single(plan.Changes);
        Assert.Equal(SchemaChangeKind.RelaxColumnNullability, relax.Kind);
        Assert.Equal("description", relax.ObjectName);
        await schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken);
        var relaxed = await schema.ReadTableAsync(table, TestCancellationToken);
        Assert.NotNull(relaxed);
        Assert.True(relaxed.Columns.Single(column => column.Name == "description").IsNullable);
        Assert.False(relaxed.Columns.Single(column => column.Name == "accountid").IsNullable);

        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "No description", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using var verify = new SqlConnection(database.ConnectionString);
        await verify.OpenAsync(TestCancellationToken);
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM [dbo].[account] WHERE [description] IS NULL;";
        Assert.Equal(1, Convert.ToInt64(await command.ExecuteScalarAsync(TestCancellationToken), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task DroppedManagedTable_IsRecreatedWithCheckpointClearedForFullRead()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var stateStore = new SqlServerReplicationStateStore(database.ConnectionString);
        var created = await stateStore.GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.NotEqual(TableState.ResyncRequired, created?.State);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "Existing", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE [dbo].[account];";
            _ = await drop.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync(table, TestCancellationToken), new SchemaPolicy());
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.CreateTable);
        await schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken);

        var state = await stateStore.GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.NotNull(state);
        Assert.Null(state.DataCheckpoint);
        Assert.Equal(TableState.ResyncRequired, state.State);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task DecimalScaleIncrease_WidensColumnAndPreservesValues()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        static TableDefinition AmountTable(int scale) => new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition
                {
                    LogicalName = "amount",
                    SourceType = SourceType.Decimal,
                    IsNullable = true,
                    Precision = 38,
                    Scale = scale,
                    MaxIntegerDigits = 12
                }
            ]);
        var narrow = AmountTable(2);
        await PrepareTableAsync(database.ConnectionString, narrow);
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        var id = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync("integration", narrow, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(
                new SourcePage(
                    [new SourceRecord(id, ChangeKind.Upsert, new Dictionary<string, object?> { ["amount"] = 99999999999.25m })],
                    null,
                    null,
                    false),
                TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        var widened = AmountTable(4);
        var plan = SchemaPlanner.Plan(widened, await schema.ReadTableAsync(widened, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(SchemaChangeKind.ExpandColumn, Assert.Single(plan.Changes).Kind);
        await schema.ApplySchemaPlanAsync("integration", widened, plan, TestCancellationToken);

        var applied = await schema.ReadTableAsync(widened, TestCancellationToken);
        Assert.NotNull(applied);
        Assert.Equal(4, applied.Columns.Single(column => string.Equals(column.Name, "amount", StringComparison.OrdinalIgnoreCase)).Scale);
        Assert.Empty(SchemaPlanner.Plan(widened, applied, new SchemaPolicy()).Changes);

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [amount] FROM [dbo].[account];";
        Assert.Equal(99999999999.25m, (decimal)(await command.ExecuteScalarAsync(TestCancellationToken))!);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task ColumnThatBecomesUnsupported_IsRetainedWithExistingValues()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        static TableDefinition CodesTable(bool supported) => new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, IsNullable = true, MaxLength = 100 },
                new ColumnDefinition
                {
                    LogicalName = "legacycode",
                    SourceType = SourceType.String,
                    IsNullable = true,
                    MaxLength = 20,
                    UnsupportedReason = supported ? null : "Dataverse attribute 'legacycode' is not valid for read operations."
                }
            ]);
        await PrepareTableAsync(database.ConnectionString, CodesTable(true));
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        var id = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync("integration", CodesTable(true), TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(
                new SourcePage(
                    [new SourceRecord(id, ChangeKind.Upsert, new Dictionary<string, object?> { ["name"] = "First", ["legacycode"] = "KEEP" })],
                    null,
                    null,
                    false),
                TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        var unsupported = CodesTable(false);
        var plan = SchemaPlanner.Plan(unsupported, await schema.ReadTableAsync(unsupported, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(SchemaChangeKind.UnsupportedColumnRetained, Assert.Single(plan.Changes).Kind);
        await schema.ApplySchemaPlanAsync("integration", unsupported, plan, TestCancellationToken);
        await using (var session = await writer.BeginIncrementalSyncAsync("integration", unsupported, "checkpoint-1", TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(
                new SourcePage(
                    [new SourceRecord(id, ChangeKind.Upsert, new Dictionary<string, object?> { ["name"] = "Updated" })],
                    null,
                    null,
                    false),
                TestCancellationToken);
            await session.CommitAsync("checkpoint-2", new(1, 1, 0, 1, 0), TestCancellationToken);
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [name] + N'|' + [legacycode] FROM [dbo].[account];";
        Assert.Equal("Updated|KEEP", await command.ExecuteScalarAsync(TestCancellationToken));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task RetainedLegacyColumn_IsRelaxedSoNewRowsCanBeInserted()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await new SqlServerMetadataStore(database.ConnectionString).EnsureCreatedAsync(TestCancellationToken);
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        await schema.ApplySchemaPlanAsync(
            "integration",
            table,
            SchemaPlanner.Plan(table, null, new SchemaPolicy(), SynchronizationMode.NoDataLoss),
            TestCancellationToken);
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = "ALTER TABLE [dbo].[account] ADD [legacy] nvarchar(20) NOT NULL CONSTRAINT [DF_account_legacy] DEFAULT N'x'; ALTER TABLE [dbo].[account] DROP CONSTRAINT [DF_account_legacy];";
            _ = await legacy.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync(table, TestCancellationToken), new SchemaPolicy(), SynchronizationMode.NoDataLoss);
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.SourceColumnRemoved && change.ObjectName.Equals("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.RelaxColumnNullability && change.ObjectName.Equals("legacy", StringComparison.OrdinalIgnoreCase));
        await schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken);

        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync(
            "integration", table, TestCancellationToken, replaceExisting: false, retainDeletedRows: true, mode: SynchronizationMode.NoDataLoss))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "New row", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }
        var relaxed = await schema.ReadTableAsync(table, TestCancellationToken);
        var legacyColumn = relaxed!.Columns.Single(column => column.Name == "legacy");
        Assert.True(legacyColumn.IsNullable);
        Assert.Equal(20, legacyColumn.MaxLength);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task NarrowerSourceColumn_KeepsWiderDestinationAndContinuesSyncing()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        await PrepareTableAsync(database.ConnectionString, AccountsTable(nameLength: 200));
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        var narrowed = AccountsTable(nameLength: 100);
        var plan = SchemaPlanner.Plan(narrowed, await schema.ReadTableAsync(narrowed, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(SchemaChangeKind.NarrowerSourceColumn, Assert.Single(plan.Changes).Kind);
        await schema.ApplySchemaPlanAsync("integration", narrowed, plan, TestCancellationToken);
        await using (var session = await writer.BeginInitialSyncAsync("integration", narrowed, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "Short", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        var destination = await schema.ReadTableAsync(narrowed, TestCancellationToken);
        Assert.Equal(200, destination!.Columns.Single(column => string.Equals(column.Name, "name", StringComparison.OrdinalIgnoreCase)).MaxLength);
    }

    [Theory]
    [Trait("Category", "SqlServerIntegration")]
    [InlineData(SynchronizationMode.Complete)]
    [InlineData(SynchronizationMode.NoDataLoss)]
    public async Task ColumnWithChangedType_IsReplacedAccordingToMode(SynchronizationMode mode)
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        static TableDefinition CodeTable(bool numeric) => new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, IsNullable = true, MaxLength = 100 },
                numeric
                    ? new ColumnDefinition { LogicalName = "code", SourceType = SourceType.Int32, IsNullable = true }
                    : new ColumnDefinition { LogicalName = "code", SourceType = SourceType.String, IsNullable = true, MaxLength = 20 }
            ]);
        await new SqlServerMetadataStore(database.ConnectionString).EnsureCreatedAsync(TestCancellationToken);
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        await schema.ApplySchemaPlanAsync("integration", CodeTable(false), SchemaPlanner.Plan(CodeTable(false), null, new SchemaPolicy(), mode), TestCancellationToken);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        var stateStore = new SqlServerReplicationStateStore(database.ConnectionString);
        var noDataLoss = mode == SynchronizationMode.NoDataLoss;
        var id = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync(
            "integration", CodeTable(false), TestCancellationToken, replaceExisting: !noDataLoss, retainDeletedRows: noDataLoss, mode: mode))
        {
            _ = await session.ApplyPageAsync(
                new SourcePage([new SourceRecord(id, ChangeKind.Upsert, new Dictionary<string, object?> { ["name"] = "Row", ["code"] = "A-1" })], null, null, false),
                TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        var replaced = CodeTable(true);
        var plan = SchemaPlanner.Plan(
            replaced,
            await schema.ReadTableAsync(replaced, TestCancellationToken),
            new SchemaPolicy(),
            mode,
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));
        var replacement = Assert.Single(plan.Changes, change => change.Kind == SchemaChangeKind.ReplaceColumn);
        Assert.Equal(noDataLoss ? "code_replaced_20260930" : null, replacement.NewObjectName);
        await schema.ApplySchemaPlanAsync("integration", replaced, plan, TestCancellationToken);

        var state = await stateStore.GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.Null(state!.DataCheckpoint);
        var destination = await schema.ReadTableAsync(replaced, TestCancellationToken);
        Assert.Equal(SourceType.Int32, destination!.Columns.Single(column => string.Equals(column.Name, "code", StringComparison.OrdinalIgnoreCase)).SourceType);
        Assert.Equal(noDataLoss, destination.Columns.Any(column => string.Equals(column.Name, "code_replaced_20260930", StringComparison.OrdinalIgnoreCase)));

        await using (var session = await writer.BeginInitialSyncAsync(
            "integration", replaced, TestCancellationToken, replaceExisting: !noDataLoss, retainDeletedRows: noDataLoss, mode: mode))
        {
            _ = await session.ApplyPageAsync(
                new SourcePage([new SourceRecord(id, ChangeKind.Upsert, new Dictionary<string, object?> { ["name"] = "Row", ["code"] = 42 })], null, null, false),
                TestCancellationToken);
            await session.CommitAsync("checkpoint-2", new(1, 1, 0, 1, 0), TestCancellationToken);
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = noDataLoss
            ? "SELECT CONCAT([code], N'|', [code_replaced_20260930]) FROM [dbo].[account];"
            : "SELECT CONCAT([code], N'|') FROM [dbo].[account];";
        Assert.Equal(noDataLoss ? "42|A-1" : "42|", await command.ExecuteScalarAsync(TestCancellationToken));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task DateTimeBehaviorChange_ReplacesOnlyColumnsWhoseStorageDiffers()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        static TableDefinition EventTable(DateTimeBehavior userLocal, DateTimeBehavior independent) => new TableDefinition(
            "event",
            "events",
            "event",
            [
                new ColumnDefinition { LogicalName = "eventid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "userlocal", SourceType = SourceType.DateTime, IsNullable = true, DateTimeBehavior = userLocal },
                new ColumnDefinition { LogicalName = "independent", SourceType = SourceType.DateTime, IsNullable = true, DateTimeBehavior = independent },
                new ColumnDefinition { LogicalName = "dayonly", SourceType = SourceType.DateTime, IsNullable = true, DateTimeBehavior = DateTimeBehavior.DateOnly }
            ]);
        await PrepareTableAsync(database.ConnectionString, EventTable(DateTimeBehavior.UserLocal, DateTimeBehavior.TimeZoneIndependent));
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var unchanged = EventTable(DateTimeBehavior.UserLocal, DateTimeBehavior.TimeZoneIndependent);
        Assert.Empty(SchemaPlanner.Plan(unchanged, await schema.ReadTableAsync(unchanged, TestCancellationToken), new SchemaPolicy()).Changes);

        var changed = EventTable(DateTimeBehavior.DateOnly, DateTimeBehavior.UserLocal);
        var plan = SchemaPlanner.Plan(changed, await schema.ReadTableAsync(changed, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(
            ["userlocal"],
            plan.Changes.Where(change => change.Kind == SchemaChangeKind.ReplaceColumn).Select(change => change.ObjectName).Order(StringComparer.Ordinal));
        await schema.ApplySchemaPlanAsync("integration", changed, plan, TestCancellationToken);
        Assert.Empty(SchemaPlanner.Plan(changed, await schema.ReadTableAsync(changed, TestCancellationToken), new SchemaPolicy()).Changes);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task TableLock_DetectsLostLockSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var provider = new SqlServerProvider();
        await using var tableLock = await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken);
        await tableLock.EnsureHeldAsync(TestCancellationToken);

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var sessions = connection.CreateCommand();
            sessions.CommandText = "SELECT session_id FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND is_user_process = 1 AND session_id <> @@SPID;";
            var ids = new List<short>();
            await using (var reader = await sessions.ExecuteReaderAsync(TestCancellationToken))
            {
                while (await reader.ReadAsync(TestCancellationToken))
                {
                    ids.Add(reader.GetInt16(0));
                }
            }

            Assert.NotEmpty(ids);
            foreach (var id in ids)
            {
                await using var kill = connection.CreateCommand();
                kill.CommandText = $"KILL {id};";
                _ = await kill.ExecuteNonQueryAsync(TestCancellationToken);
            }
        }

        await Assert.ThrowsAsync<TableLockLostException>(() => tableLock.EnsureHeldAsync(TestCancellationToken));
        await using var replacement = await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken);
        await replacement.EnsureHeldAsync(TestCancellationToken);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task MetadataStore_ConcurrentFirstRunsAllSucceed()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var store = new SqlServerMetadataStore(database.ConnectionString);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.EnsureCreatedAsync(TestCancellationToken))));
        await store.EnsureCreatedAsync(TestCancellationToken);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task ConcurrentSession_ForSameJobAndTableIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using var first = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken);

        var error = await Assert.ThrowsAsync<SynchronizationAlreadyRunningException>(() => writer.BeginInitialSyncAsync(
            "integration",
            table,
            TestCancellationToken));

        Assert.Contains("already running", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task SourceFailure_RollsBackDataAndRecordsDurableFailedRun()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var engine = new ReplicationEngine(
            new ThrowingAfterPageSource(Page(Upsert(Guid.NewGuid(), "Uncommitted", 1))),
            new SqlServerDestinationWriter(database.ConnectionString),
            new SqlServerReplicationStateStore(database.ConnectionString));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => engine.SyncAsync("integration", table, 100, TestCancellationToken));

        Assert.Equal(ErrorCategory.Synchronization, exception.Category);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*) FROM [dbo].[account];
            SELECT [Status], [ChangeCheckpoint] FROM [replicera].[Tables] WHERE [DataverseLogicalName] = N'account';
            SELECT [Status], [ErrorCode], [ErrorMessage] FROM [replicera].[SyncRuns];
            SELECT COUNT_BIG(*) FROM sys.tables WHERE [name] LIKE N'replicera_stage_%';
            """;
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(0, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Failed", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Failed", reader.GetString(0));
        Assert.Equal("Synchronization", reader.GetString(1));
        Assert.Equal("Synchronization failed before the checkpoint could be committed.", reader.GetString(2));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(0, reader.GetInt64(0));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Status_JsonOutputReportsPersistedTableState()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            await session.CommitAsync("checkpoint", new(0, 0, 0, 0, 0), TestCancellationToken);
        }

        var directory = Directory.CreateTempSubdirectory("replicera-status-");
        var configPath = Path.Combine(directory.FullName, "replicera.json");
        var connectionVariable = $"REPLICERA_SQL_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(connectionVariable, database.ConnectionString);
            await ConfigurationFile.SaveAsync(
                configPath,
                Configuration(connectionVariable),
                TestCancellationToken);
            using var output = new StringWriter();

            var exitCode = await CliApplication.RunAsync(
                ["status", "--job", "integration", "--json", "--config", configPath],
                output,
                TextWriter.Null,
                TestCancellationToken);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            var status = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("integration", status.GetProperty("job").GetString());
            Assert.Equal("account", status.GetProperty("table").GetString());
            Assert.Equal("Healthy", status.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.String, status.GetProperty("lastSuccessfulSyncUtc").ValueKind);
            Assert.Equal("Initial", status.GetProperty("lastRunType").GetString());
            Assert.Equal(JsonValueKind.String, status.GetProperty("lastRunStartedUtc").ValueKind);
            Assert.Equal(JsonValueKind.String, status.GetProperty("lastRunCompletedUtc").ValueKind);
            Assert.Equal(0, status.GetProperty("recordsInserted").GetInt64());
            Assert.Equal(0, status.GetProperty("recordsUpdated").GetInt64());
            Assert.Equal(0, status.GetProperty("recordsDeleted").GetInt64());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("errorCode").ValueKind);
            Assert.DoesNotContain("checkpoint", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionVariable, null);
            directory.Delete(true);
        }
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task MultiPageSync_LoadsEveryPageAndCommitsTerminalCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var pages = Enumerable.Range(0, 3)
            .Select(pageIndex => new SourcePage(
                Enumerable.Range(0, 2_000)
                    .Select(rowIndex => Upsert(Guid.NewGuid(), $"Account {pageIndex:D2}-{rowIndex:D4}", rowIndex))
                    .ToArray(),
                pageIndex < 2 ? $"page-{pageIndex + 2}" : null,
                pageIndex == 2 ? "terminal-checkpoint" : null,
                pageIndex < 2))
            .ToArray();
        var engine = new ReplicationEngine(
            new PageSource(pages),
            new SqlServerDestinationWriter(database.ConnectionString),
            new SqlServerReplicationStateStore(database.ConnectionString));

        var metrics = await engine.SyncAsync("integration", table, 2_000, TestCancellationToken);

        Assert.Equal(3, metrics.PagesProcessed);
        Assert.Equal(6_000, metrics.RecordsReceived);
        Assert.Equal(6_000, metrics.RecordsInserted);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*) FROM [dbo].[account];
            SELECT [ChangeCheckpoint] FROM [replicera].[Tables] WHERE [DataverseLogicalName] = N'account';
            """;
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(6_000, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("terminal-checkpoint", reader.GetString(0));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task SecondInitialSync_AtomicallyReplacesExistingRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using (var first = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await first.ApplyPageAsync(
                Page(Upsert(Guid.NewGuid(), "Old 1", 1), Upsert(Guid.NewGuid(), "Old 2", 2)),
                TestCancellationToken);
            await first.CommitAsync("old-checkpoint", new(1, 2, 2, 0, 0), TestCancellationToken);
        }

        var replacementId = Guid.NewGuid();
        await using (var replacement = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await replacement.ApplyPageAsync(Page(Upsert(replacementId, "Replacement", 3)), TestCancellationToken);
            await replacement.CommitAsync("replacement-checkpoint", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [accountid], [name] FROM [dbo].[account];";
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(replacementId, reader.GetGuid(0));
        Assert.Equal("Replacement", reader.GetString(1));
        Assert.False(await reader.ReadAsync(TestCancellationToken));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task SupportedValues_RoundTripAtBoundariesAndPreserveNulls()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = FidelityTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var populatedId = Guid.NewGuid();
        var nullId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var longText = new string('Ω', 5_001);
        var moment = new DateTime(2026, 9, 19, 12, 34, 56, 789, DateTimeKind.Unspecified).AddTicks(1_234);
        var page = new SourcePage(
            [
                new SourceRecord(
                    populatedId,
                    ChangeKind.Upsert,
                    new Dictionary<string, object?>
                    {
                        ["shorttext"] = "Unicode ✓",
                        ["longtext"] = longText,
                        ["flag"] = true,
                        ["whole"] = int.MaxValue,
                        ["big"] = long.MaxValue,
                        ["amount"] = 123456789012345678.1234567890m,
                        ["ratio"] = 1.0d / 3.0d,
                        ["money"] = 123456789012345.6789m,
                        ["sampledate"] = new DateTime(2026, 9, 19, 23, 59, 59, DateTimeKind.Utc),
                        ["moment"] = moment,
                        ["categories"] = new ChoiceSetValue([1, 3]),
                        ["ownerid"] = new LookupValue(ownerId, "team")
                    }),
                new SourceRecord(nullId, ChangeKind.Upsert, new Dictionary<string, object?>())
            ],
            null,
            "fidelity-checkpoint",
            false);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            var applied = await session.ApplyPageAsync(page, TestCancellationToken);
            Assert.Equal(2, applied.Inserted);
            await session.CommitAsync("fidelity-checkpoint", new(1, 2, 2, 0, 0), TestCancellationToken);
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [shorttext], [longtext], [flag], [whole], [big], [amount], [ratio], [money],
                   [sampledate], [moment], [categories], [ownerid], [ownerid_type]
            FROM [dbo].[fidelity] WHERE [fidelityid] = @populatedId;
            SELECT [shorttext], [longtext], [ownerid], [ownerid_type]
            FROM [dbo].[fidelity] WHERE [fidelityid] = @nullId;
            """;
        _ = command.Parameters.AddWithValue("@populatedId", populatedId);
        _ = command.Parameters.AddWithValue("@nullId", nullId);
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Unicode ✓", reader.GetString(0));
        Assert.Equal(longText, reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(int.MaxValue, reader.GetInt32(3));
        Assert.Equal(long.MaxValue, reader.GetInt64(4));
        Assert.Equal(123456789012345678.1234567890m, reader.GetDecimal(5));
        Assert.Equal(1.0d / 3.0d, reader.GetDouble(6), 12);
        Assert.Equal(123456789012345.6789m, reader.GetDecimal(7));
        Assert.Equal(new DateTime(2026, 9, 19), reader.GetDateTime(8));
        Assert.Equal(moment, reader.GetDateTime(9));
        Assert.Equal("[1,3]", reader.GetString(10));
        Assert.Equal(ownerId, reader.GetGuid(11));
        Assert.Equal("team", reader.GetString(12));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task CheckpointPersistenceFailure_RollsBackAppliedRowsAndStagingTable()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER [replicera].[RejectCheckpointUpdate]
                ON [replicera].[Tables]
                AFTER UPDATE
                AS
                BEGIN
                    IF UPDATE([ChangeCheckpoint])
                        THROW 51000, 'Injected checkpoint persistence failure.', 1;
                END;
                """;
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(
                Page(Upsert(Guid.NewGuid(), "Must roll back", 1)),
                TestCancellationToken);
            _ = await Assert.ThrowsAsync<SqlException>(() => session.CommitAsync(
                "rejected-checkpoint",
                new SyncMetrics(1, 1, 1, 0, 0),
                TestCancellationToken));
        }

        await using var verification = new SqlConnection(database.ConnectionString);
        await verification.OpenAsync(TestCancellationToken);
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText = """
            SELECT COUNT_BIG(*) FROM [dbo].[account];
            SELECT [ChangeCheckpoint] FROM [replicera].[Tables] WHERE [DataverseLogicalName] = N'account';
            SELECT COUNT_BIG(*) FROM sys.tables WHERE [name] LIKE N'replicera_stage_%';
            """;
        await using var reader = await verifyCommand.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(0, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.True(reader.IsDBNull(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(0, reader.GetInt64(0));
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task AbruptConnectionTermination_RollsBackRowsCheckpointAndStagingTable()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new SqlServerDestinationWriter(database.ConnectionString);
        var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken);
        _ = await session.ApplyPageAsync(
            Page(Upsert(Guid.NewGuid(), "Must roll back after kill", 1)),
            TestCancellationToken);

        await using (var administrator = new SqlConnection(database.ConnectionString))
        {
            await administrator.OpenAsync(TestCancellationToken);
            await using var findSession = administrator.CreateCommand();
            findSession.CommandText = """
                SELECT TOP (1) [request_session_id]
                FROM sys.dm_tran_locks
                WHERE [resource_type] = N'APPLICATION'
                  AND [request_owner_type] = N'TRANSACTION'
                  AND [request_status] = N'GRANT'
                  AND [request_session_id] <> @@SPID;
                """;
            var sessionId = Convert.ToInt32(
                await findSession.ExecuteScalarAsync(TestCancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            await using var kill = administrator.CreateCommand();
            kill.CommandText = $"KILL {sessionId};";
            _ = await kill.ExecuteNonQueryAsync(TestCancellationToken);
        }

        await session.DisposeAsync();

        await using var verification = new SqlConnection(database.ConnectionString);
        await verification.OpenAsync(TestCancellationToken);
        await using var command = verification.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*) FROM [dbo].[account];
            SELECT [ChangeCheckpoint] FROM [replicera].[Tables] WHERE [DataverseLogicalName] = N'account';
            SELECT COUNT_BIG(*) FROM sys.tables WHERE [name] LIKE N'replicera_stage_%';
            """;
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(0, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.True(reader.IsDBNull(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal(0, reader.GetInt64(0));
    }

    private static async Task PrepareTableAsync(string connectionString, TableDefinition table)
    {
        await new SqlServerMetadataStore(connectionString).EnsureCreatedAsync(TestCancellationToken);
        var schema = new SqlServerSchemaManager(connectionString);
        var current = await schema.ReadTableAsync(table, TestCancellationToken);
        var plan = SchemaPlanner.Plan(table, current, new SchemaPolicy());
        await schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken);
    }

    private static RepliceraConfiguration Configuration(string connectionVariable) => new()
    {
        Sources =
        [
            new SourceConfiguration
            {
                Name = "source",
                Url = new Uri("https://example.crm.dynamics.com"),
                TenantId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                Authentication = new AuthenticationConfiguration
                {
                    Method = AuthenticationMethod.ClientSecret,
                    SecretEnvironmentVariable = "UNUSED_DATAVERSE_SECRET"
                }
            }
        ],
        Destinations =
        [
            new DestinationConfiguration
            {
                Name = "sql",
                Provider = "sqlserver",
                ConnectionStringEnvironmentVariable = connectionVariable
            }
        ],
        Jobs =
        [
            new JobConfiguration
            {
                Name = "integration",
                Source = "source",
                Destination = "sql",
                Tables = ["account"]
            }
        ]
    };

    private static TableDefinition AccountsTable(int nameLength = 200, bool includeDescription = false) => new(
        "account",
        "accounts",
        "account",
        new List<ColumnDefinition>
        {
            new ColumnDefinition
            {
                LogicalName = "accountid",
                SourceType = SourceType.Guid,
                IsPrimaryKey = true
            },
            new ColumnDefinition
            {
                LogicalName = "name",
                SourceType = SourceType.String,
                IsNullable = true,
                MaxLength = nameLength
            },
            new ColumnDefinition
            {
                LogicalName = "statuscode",
                SourceType = SourceType.Choice,
                IsNullable = true
            }
        }.Concat(includeDescription
            ?
            [
                new ColumnDefinition
                {
                    LogicalName = "description",
                    SourceType = SourceType.String,
                    IsNullable = false,
                    MaxLength = 1_000
                }
            ]
            : []));

    private static TableDefinition FidelityTable() => new(
        "fidelity",
        "fidelities",
        "fidelity",
        [
            new ColumnDefinition { LogicalName = "fidelityid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new ColumnDefinition { LogicalName = "shorttext", SourceType = SourceType.String, MaxLength = 20, IsNullable = true },
            new ColumnDefinition { LogicalName = "longtext", SourceType = SourceType.Text, IsNullable = true },
            new ColumnDefinition { LogicalName = "flag", SourceType = SourceType.Boolean, IsNullable = true },
            new ColumnDefinition { LogicalName = "whole", SourceType = SourceType.Int32, IsNullable = true },
            new ColumnDefinition { LogicalName = "big", SourceType = SourceType.Int64, IsNullable = true },
            new ColumnDefinition { LogicalName = "amount", SourceType = SourceType.Decimal, Precision = 38, Scale = 10, IsNullable = true },
            new ColumnDefinition { LogicalName = "ratio", SourceType = SourceType.Double, IsNullable = true },
            new ColumnDefinition { LogicalName = "money", SourceType = SourceType.Money, Precision = 19, Scale = 4, IsNullable = true },
            new ColumnDefinition
            {
                LogicalName = "sampledate",
                SourceType = SourceType.DateTime,
                DateTimeBehavior = DateTimeBehavior.DateOnly,
                IsNullable = true
            },
            new ColumnDefinition
            {
                LogicalName = "moment",
                SourceType = SourceType.DateTime,
                DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent,
                IsNullable = true
            },
            new ColumnDefinition { LogicalName = "categories", SourceType = SourceType.MultiSelectChoice, IsNullable = true },
            new ColumnDefinition
            {
                LogicalName = "ownerid",
                SourceType = SourceType.Lookup,
                LookupTargets = ["systemuser", "team"],
                IsNullable = true
            }
        ]);

    private static SourcePage Page(params SourceRecord[] records) => new(records, null, null, false);

    private static SourceRecord Upsert(Guid id, string name, int status) => new(
        id,
        ChangeKind.Upsert,
        new Dictionary<string, object?>
        {
            ["name"] = name,
            ["statuscode"] = status
        });

    private static SourceRecord Delete(Guid id) => new(id, ChangeKind.Delete, new Dictionary<string, object?>());

    private sealed class ThrowingAfterPageSource(SourcePage page) : ISourceChangeReader
    {
        public async IAsyncEnumerable<SourcePage> ReadChangesAsync(
            TableDefinition table,
            string? dataCheckpoint,
            int pageSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return page;
            await Task.Yield();
            throw new InvalidOperationException("Injected source read failure.");
        }
    }

    private sealed class PageSource(IEnumerable<SourcePage> pages) : ISourceChangeReader
    {
        public async IAsyncEnumerable<SourcePage> ReadChangesAsync(
            TableDefinition table,
            string? dataCheckpoint,
            int pageSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return page;
                await Task.Yield();
            }
        }
    }

    private sealed class TestDatabase(string adminConnectionString, string connectionString, string databaseName) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public static async Task<TestDatabase?> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            var adminBuilder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
            var databaseName = $"replicera_{Guid.NewGuid():N}";
            await using var connection = new SqlConnection(adminBuilder.ConnectionString);
            await connection.OpenAsync(TestCancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}];";
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
            var databaseBuilder = new SqlConnectionStringBuilder(adminBuilder.ConnectionString) { InitialCatalog = databaseName };
            return new TestDatabase(adminBuilder.ConnectionString, databaseBuilder.ConnectionString, databaseName);
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(adminConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
