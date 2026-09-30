using System.Text.Json;
using Npgsql;
using Replicera.Cli;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Core.Schema;
using Replicera.Provider.PostgreSql;

namespace Replicera.IntegrationTests;

public sealed class PostgreSqlProviderIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "REPLICERA_POSTGRES_TEST_CONNECTION_STRING";
    private static readonly CancellationToken TestCancellationToken = CancellationToken.None;

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task TableLockAndDependencyPreflight_ProtectSchemaLifecycle()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var provider = new PostgreSqlProvider();
        await using (var tableLock = await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken))
        {
            await Assert.ThrowsAsync<Replicera.Core.Errors.SynchronizationAlreadyRunningException>(async () =>
                await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken));
        }

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = new NpgsqlCommand("CREATE VIEW public.account_view AS SELECT accountid FROM public.account;", connection);
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        var current = await schema.ReadTableAsync("integration", table, TestCancellationToken);
        var plan = SchemaPlanner.Plan(table, current, new SchemaPolicy(), SynchronizationMode.Reload);
        var error = await Assert.ThrowsAsync<RepliceraException>(() =>
            schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken));
        Assert.Equal(ErrorCategory.SchemaConflict, error.Category);
        Assert.Contains("account_view", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task StateStore_BeforeFirstSyncReturnsUninitializedState()
    {
        await using var database = await TestDatabase.CreateAsync();

        Assert.Null(await new PostgreSqlReplicationStateStore(database.ConnectionString)
            .GetTableStateAsync("integration", "account", TestCancellationToken));
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task InitialAndIncrementalSync_CommitRowsMetricsAndCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            Assert.Equal(new(2, 0, 0), await session.ApplyPageAsync(
                Page(Upsert(firstId, "First", 1), Upsert(secondId, "Second", 2)), TestCancellationToken));
            await session.CommitAsync("checkpoint-1", new(1, 2, 2, 0, 0), TestCancellationToken);
        }

        await using (var session = await writer.BeginIncrementalSyncAsync(
            "integration", table, "checkpoint-1", TestCancellationToken))
        {
            Assert.Equal(new(1, 1, 1), await session.ApplyPageAsync(
                Page(Upsert(firstId, "First updated", 3), Delete(secondId), Upsert(thirdId, "Third", 4)),
                TestCancellationToken));
            await session.CommitAsync("checkpoint-2", new(1, 3, 1, 1, 1), TestCancellationToken);
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT accountid, name, statuscode FROM public.account ORDER BY name;
            SELECT change_checkpoint, status FROM replicera.tables WHERE dataverse_logical_name = 'account';
            SELECT records_inserted, records_updated, records_deleted, pages_processed, status
            FROM replicera.sync_runs WHERE sync_type = 'Incremental';
            """, connection);
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
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DisposedSession_RollsBackRowsCheckpointAndTemporaryStaging()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        var id = Guid.NewGuid();
        await using (var initial = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await initial.ApplyPageAsync(Page(Upsert(id, "Original", 1)), TestCancellationToken);
            await initial.CommitAsync("stable", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using (var interrupted = await writer.BeginIncrementalSyncAsync(
            "integration", table, "stable", TestCancellationToken))
        {
            _ = await interrupted.ApplyPageAsync(Page(Upsert(id, "Uncommitted", 2)), TestCancellationToken);
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT name FROM public.account WHERE accountid = @id;
            SELECT change_checkpoint FROM replicera.tables WHERE dataverse_logical_name = 'account';
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Original", reader.GetString(0));
        Assert.True(await reader.NextResultAsync(TestCancellationToken));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("stable", reader.GetString(0));
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DroppedManagedTable_IsRecreatedWithCheckpointClearedForFullRead()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var stateStore = new PostgreSqlReplicationStateStore(database.ConnectionString);
        var created = await stateStore.GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.NotEqual(TableState.ResyncRequired, created?.State);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "Existing", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var drop = new NpgsqlCommand("DROP TABLE public.account;", connection);
            _ = await drop.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync("integration", table, TestCancellationToken), new SchemaPolicy());
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.CreateTable);
        await schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken);

        var state = await stateStore.GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.NotNull(state);
        Assert.Null(state.DataCheckpoint);
        Assert.Equal(TableState.ResyncRequired, state.State);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DecimalScaleIncrease_WidensColumnAndPreservesValues()
    {
        await using var database = await TestDatabase.CreateAsync();

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
        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
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
        var plan = SchemaPlanner.Plan(widened, await schema.ReadTableAsync("integration", widened, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(SchemaChangeKind.ExpandColumn, Assert.Single(plan.Changes).Kind);
        await schema.ApplySchemaPlanAsync("integration", widened, plan, TestCancellationToken);

        var applied = await schema.ReadTableAsync("integration", widened, TestCancellationToken);
        Assert.NotNull(applied);
        Assert.Equal(4, applied.Columns.Single(column => string.Equals(column.Name, "amount", StringComparison.OrdinalIgnoreCase)).Scale);
        Assert.Empty(SchemaPlanner.Plan(widened, applied, new SchemaPolicy()).Changes);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = new NpgsqlCommand("SELECT amount FROM public.account;", connection);
        Assert.Equal(99999999999.25m, (decimal)(await command.ExecuteScalarAsync(TestCancellationToken))!);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ColumnThatBecomesUnsupported_IsRetainedWithExistingValues()
    {
        await using var database = await TestDatabase.CreateAsync();

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
        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
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
        var plan = SchemaPlanner.Plan(unsupported, await schema.ReadTableAsync("integration", unsupported, TestCancellationToken), new SchemaPolicy());
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

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = new NpgsqlCommand("SELECT name || '|' || legacycode FROM public.account;", connection);
        Assert.Equal("Updated|KEEP", await command.ExecuteScalarAsync(TestCancellationToken));
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task RetainedLegacyColumn_IsRelaxedSoNewRowsCanBeInserted()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = AccountsTable();
        await new PostgreSqlMetadataStore(database.ConnectionString).EnsureCreatedAsync(TestCancellationToken);
        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        await schema.ApplySchemaPlanAsync(
            "integration",
            table,
            SchemaPlanner.Plan(table, null, new SchemaPolicy(), SynchronizationMode.NoDataLoss),
            TestCancellationToken);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var legacy = new NpgsqlCommand(
                "ALTER TABLE public.account ADD COLUMN legacy character varying(20) NOT NULL DEFAULT 'x'; ALTER TABLE public.account ALTER COLUMN legacy DROP DEFAULT;",
                connection);
            _ = await legacy.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync("integration", table, TestCancellationToken), new SchemaPolicy(), SynchronizationMode.NoDataLoss);
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.SourceColumnRemoved && change.ObjectName.Equals("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.RelaxColumnNullability && change.ObjectName.Equals("legacy", StringComparison.OrdinalIgnoreCase));
        await schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken);

        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        await using (var session = await writer.BeginInitialSyncAsync(
            "integration", table, TestCancellationToken, replaceExisting: false, retainDeletedRows: true, mode: SynchronizationMode.NoDataLoss))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "New row", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }
        var relaxed = await schema.ReadTableAsync("integration", table, TestCancellationToken);
        Assert.True(relaxed!.Columns.Single(column => column.Name == "legacy").IsNullable);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task NarrowerSourceColumn_KeepsWiderDestinationAndContinuesSyncing()
    {
        await using var database = await TestDatabase.CreateAsync();

        await PrepareTableAsync(database.ConnectionString, AccountsTable(nameLength: 200));
        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        var narrowed = AccountsTable(nameLength: 100);
        var plan = SchemaPlanner.Plan(narrowed, await schema.ReadTableAsync("integration", narrowed, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(SchemaChangeKind.NarrowerSourceColumn, Assert.Single(plan.Changes).Kind);
        await schema.ApplySchemaPlanAsync("integration", narrowed, plan, TestCancellationToken);
        await using (var session = await writer.BeginInitialSyncAsync("integration", narrowed, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "Short", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        var destination = await schema.ReadTableAsync("integration", narrowed, TestCancellationToken);
        Assert.Equal(200, destination!.Columns.Single(column => string.Equals(column.Name, "name", StringComparison.OrdinalIgnoreCase)).MaxLength);
    }

    [SkippableTheory]
    [Trait("Category", "PostgreSqlIntegration")]
    [InlineData(SynchronizationMode.Complete)]
    [InlineData(SynchronizationMode.NoDataLoss)]
    public async Task ColumnWithChangedType_IsReplacedAccordingToMode(SynchronizationMode mode)
    {
        await using var database = await TestDatabase.CreateAsync();

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
        await new PostgreSqlMetadataStore(database.ConnectionString).EnsureCreatedAsync(TestCancellationToken);
        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        await schema.ApplySchemaPlanAsync("integration", CodeTable(false), SchemaPlanner.Plan(CodeTable(false), null, new SchemaPolicy(), mode), TestCancellationToken);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        var stateStore = new PostgreSqlReplicationStateStore(database.ConnectionString);
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
            await schema.ReadTableAsync("integration", replaced, TestCancellationToken),
            new SchemaPolicy(),
            mode,
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));
        var replacement = Assert.Single(plan.Changes, change => change.Kind == SchemaChangeKind.ReplaceColumn);
        Assert.Equal(noDataLoss ? "code_replaced_20260930" : null, replacement.NewObjectName);
        await schema.ApplySchemaPlanAsync("integration", replaced, plan, TestCancellationToken);

        var state = await stateStore.GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.Null(state!.DataCheckpoint);
        var destination = await schema.ReadTableAsync("integration", replaced, TestCancellationToken);
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

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = new NpgsqlCommand(
            noDataLoss
                ? "SELECT concat(code, '|', code_replaced_20260930) FROM public.account;"
                : "SELECT concat(code, '|') FROM public.account;",
            connection);
        Assert.Equal(noDataLoss ? "42|A-1" : "42|", await command.ExecuteScalarAsync(TestCancellationToken));
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DateTimeBehaviorChange_ReplacesOnlyColumnsWhoseStorageDiffers()
    {
        await using var database = await TestDatabase.CreateAsync();

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
        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        var unchanged = EventTable(DateTimeBehavior.UserLocal, DateTimeBehavior.TimeZoneIndependent);
        Assert.Empty(SchemaPlanner.Plan(unchanged, await schema.ReadTableAsync("integration", unchanged, TestCancellationToken), new SchemaPolicy()).Changes);

        var changed = EventTable(DateTimeBehavior.DateOnly, DateTimeBehavior.UserLocal);
        var plan = SchemaPlanner.Plan(changed, await schema.ReadTableAsync("integration", changed, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(
            ["independent", "userlocal"],
            plan.Changes.Where(change => change.Kind == SchemaChangeKind.ReplaceColumn).Select(change => change.ObjectName).Order(StringComparer.Ordinal));
        await schema.ApplySchemaPlanAsync("integration", changed, plan, TestCancellationToken);
        Assert.Empty(SchemaPlanner.Plan(changed, await schema.ReadTableAsync("integration", changed, TestCancellationToken), new SchemaPolicy()).Changes);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task TableLock_DetectsLostLockSession()
    {
        await using var database = await TestDatabase.CreateAsync();

        var provider = new PostgreSqlProvider();
        await using var tableLock = await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken);
        await tableLock.EnsureHeldAsync(TestCancellationToken);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var kill = new NpgsqlCommand(
                "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid();",
                connection);
            Assert.True((long)(await kill.ExecuteScalarAsync(TestCancellationToken))! > 0);
        }

        await Assert.ThrowsAsync<TableLockLostException>(() => tableLock.EnsureHeldAsync(TestCancellationToken));
        await using var replacement = await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken);
        await replacement.EnsureHeldAsync(TestCancellationToken);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task AbruptConnectionTermination_DisposesSessionWithoutMaskingAndRollsBack()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        var session = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken);
        _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "Must roll back after termination", 1)), TestCancellationToken);

        await using (var administrator = new NpgsqlConnection(database.ConnectionString))
        {
            await administrator.OpenAsync(TestCancellationToken);
            await using var terminate = new NpgsqlCommand(
                "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid();",
                administrator);
            Assert.True((long)(await terminate.ExecuteScalarAsync(TestCancellationToken))! > 0);
        }

        await session.DisposeAsync();

        await using var verification = new NpgsqlConnection(database.ConnectionString);
        await verification.OpenAsync(TestCancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM public.account;", verification);
        Assert.Equal(0L, await count.ExecuteScalarAsync(TestCancellationToken));
        var state = await new PostgreSqlReplicationStateStore(database.ConnectionString).GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.Null(state?.DataCheckpoint);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task MetadataStore_ConcurrentFirstRunsAllSucceed()
    {
        await using var database = await TestDatabase.CreateAsync();

        var store = new PostgreSqlMetadataStore(database.ConnectionString);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.EnsureCreatedAsync(TestCancellationToken))));
        await store.EnsureCreatedAsync(TestCancellationToken);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ConcurrentSession_ForSameJobAndTableIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        await using var first = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken);

        var error = await Assert.ThrowsAsync<SynchronizationAlreadyRunningException>(() =>
            writer.BeginInitialSyncAsync("integration", table, TestCancellationToken));

        Assert.Contains("already running", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task FailureState_IsDurableAndSanitized()
    {
        await using var database = await TestDatabase.CreateAsync();

        await PrepareTableAsync(database.ConnectionString, AccountsTable());
        var store = new PostgreSqlReplicationStateStore(database.ConnectionString);
        await store.MarkFailureAsync(
            "integration",
            "account",
            TableState.Failed,
            "Synchronization",
            "Synchronization failed before the checkpoint could be committed.",
            TestCancellationToken);

        var state = await store.GetTableStateAsync("integration", "account", TestCancellationToken);
        Assert.NotNull(state);
        Assert.Equal(TableState.Failed, state.State);
        Assert.Equal("Synchronization", state.LastErrorCode);
        Assert.Equal("Synchronization failed before the checkpoint could be committed.", state.LastErrorMessage);
        Assert.Null(state.DataCheckpoint);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SchemaManager_RejectsUnmanagedTableAndAppliesSafeExpansion()
    {
        await using var unmanagedDatabase = await TestDatabase.CreateAsync();

        await using (var connection = new NpgsqlConnection(unmanagedDatabase.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var create = new NpgsqlCommand(
                "CREATE TABLE public.account (accountid uuid PRIMARY KEY, name character varying(20), statuscode integer);",
                connection);
            _ = await create.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var unmanaged = await new PostgreSqlSchemaManager(unmanagedDatabase.ConnectionString)
            .ReadTableAsync("integration", AccountsTable(20), TestCancellationToken);
        Assert.NotNull(unmanaged);
        Assert.False(unmanaged.IsManaged);
        Assert.Equal(
            SchemaChangeKind.OwnershipConflict,
            Assert.Single(SchemaPlanner.Plan(AccountsTable(20), unmanaged, new SchemaPolicy()).Changes).Kind);

        await using var managedDatabase = await TestDatabase.CreateAsync();
        Assert.NotNull(managedDatabase);
        var initial = AccountsTable(20);
        await PrepareTableAsync(managedDatabase.ConnectionString, initial);
        var expanded = AccountsTable(400, true);
        var schema = new PostgreSqlSchemaManager(managedDatabase.ConnectionString);
        var otherJob = await schema.ReadTableAsync("other-job", expanded, TestCancellationToken);
        Assert.NotNull(otherJob);
        Assert.False(otherJob.IsManaged);
        Assert.Equal("integration", otherJob.OwnerJob);
        var conflict = Assert.Single(SchemaPlanner.Plan(expanded, otherJob, new SchemaPolicy()).Changes);
        Assert.Equal(SchemaChangeKind.OwnershipConflict, conflict.Kind);
        Assert.True(conflict.IsBlocking);
        Assert.Contains("'integration'", conflict.Description, StringComparison.Ordinal);

        var plan = SchemaPlanner.Plan(
            expanded,
            await schema.ReadTableAsync("integration", expanded, TestCancellationToken),
            new SchemaPolicy());
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.ExpandColumn);
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.AddColumn);
        await schema.ApplySchemaPlanAsync("integration", expanded, plan, TestCancellationToken);
        var applied = await schema.ReadTableAsync("integration", expanded, TestCancellationToken);
        Assert.NotNull(applied);
        Assert.Equal(400, applied.Columns.Single(column => column.Name == "name").MaxLength);
        Assert.Contains(applied.Columns, column => column.Name == "description" && column.IsNullable);

        var contracted = AccountsTable(400);
        var dropPlan = SchemaPlanner.Plan(contracted, applied, new SchemaPolicy());
        Assert.Contains(dropPlan.Changes, change => change.Kind == SchemaChangeKind.DropColumn);
        await schema.ApplySchemaPlanAsync("integration", contracted, dropPlan, TestCancellationToken);
        var contractedDestination = await schema.ReadTableAsync("integration", contracted, TestCancellationToken);
        Assert.NotNull(contractedDestination);
        Assert.DoesNotContain(contractedDestination.Columns, column => column.Name == "description");
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SupportedValues_RoundTripAndStatusUsesConfiguredProvider()
    {
        await using var database = await TestDatabase.CreateAsync();

        var table = FidelityTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var moment = new DateTime(2026, 9, 20, 12, 34, 56, DateTimeKind.Unspecified);
        var instant = new DateTime(2026, 9, 20, 18, 34, 56, DateTimeKind.Utc);
        var record = new SourceRecord(
            id,
            ChangeKind.Upsert,
            new Dictionary<string, object?>
            {
                ["longtext"] = "Unicode ✓",
                ["flag"] = true,
                ["whole"] = int.MaxValue,
                ["big"] = long.MaxValue,
                ["amount"] = 123456789.1234m,
                ["ratio"] = 1.0d / 3.0d,
                ["sampledate"] = instant,
                ["moment"] = moment,
                ["instant"] = instant,
                ["categories"] = new ChoiceSetValue([1, 3]),
                ["ownerid"] = new LookupValue(ownerId, "team")
            });
        await using (var session = await new PostgreSqlDestinationWriter(database.ConnectionString)
            .BeginInitialSyncAsync("integration", table, TestCancellationToken))
        {
            Assert.Equal(1, (await session.ApplyPageAsync(Page(record), TestCancellationToken)).Inserted);
            await session.CommitAsync("fidelity-checkpoint", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT longtext, flag, whole, big, amount, ratio, sampledate, moment, instant,
                       categories, ownerid, ownerid_type
                FROM public.fidelity WHERE fidelityid = @id;
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
            Assert.True(await reader.ReadAsync(TestCancellationToken));
            Assert.Equal("Unicode ✓", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(int.MaxValue, reader.GetInt32(2));
            Assert.Equal(long.MaxValue, reader.GetInt64(3));
            Assert.Equal(123456789.1234m, reader.GetDecimal(4));
            Assert.Equal(1.0d / 3.0d, reader.GetDouble(5), 12);
            Assert.Equal(new DateOnly(2026, 9, 20), reader.GetFieldValue<DateOnly>(6));
            Assert.Equal(moment, reader.GetDateTime(7));
            Assert.Equal(instant, reader.GetDateTime(8));
            Assert.Equal("[1,3]", reader.GetString(9));
            Assert.Equal(ownerId, reader.GetGuid(10));
            Assert.Equal("team", reader.GetString(11));
        }

        var directory = Directory.CreateTempSubdirectory("replicera-postgres-status-");
        var configPath = Path.Combine(directory.FullName, "replicera.json");
        var variable = $"REPLICERA_POSTGRES_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(variable, database.ConnectionString);
            await ConfigurationFile.SaveAsync(configPath, Configuration(variable, "fidelity"), TestCancellationToken);
            using var output = new StringWriter();
            Assert.Equal(0, await CliApplication.RunAsync(
                ["status", "--job", "integration", "--json", "--config", configPath],
                output,
                TextWriter.Null,
                TestCancellationToken));
            using var document = JsonDocument.Parse(output.ToString());
            var status = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("Healthy", status.GetProperty("state").GetString());
            Assert.Equal("Initial", status.GetProperty("lastRunType").GetString());
            Assert.DoesNotContain("fidelity-checkpoint", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            directory.Delete(true);
        }
    }

    private static async Task PrepareTableAsync(string connectionString, TableDefinition table)
    {
        await new PostgreSqlMetadataStore(connectionString).EnsureCreatedAsync(TestCancellationToken);
        var schema = new PostgreSqlSchemaManager(connectionString);
        var plan = SchemaPlanner.Plan(
            table,
            await schema.ReadTableAsync("integration", table, TestCancellationToken),
            new SchemaPolicy());
        await schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken);
    }

    private static TableDefinition AccountsTable(int nameLength = 200, bool includeDescription = false) => new(
        "account",
        "accounts",
        "account",
        new List<ColumnDefinition>
        {
            new() { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new() { LogicalName = "name", SourceType = SourceType.String, IsNullable = true, MaxLength = nameLength },
            new() { LogicalName = "statuscode", SourceType = SourceType.Choice, IsNullable = true }
        }.Concat(includeDescription
            ? [new ColumnDefinition { LogicalName = "description", SourceType = SourceType.String, IsNullable = false, MaxLength = 1000 }]
            : []));

    private static TableDefinition FidelityTable() => new(
        "fidelity",
        "fidelities",
        "fidelity",
        [
            new ColumnDefinition { LogicalName = "fidelityid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new ColumnDefinition { LogicalName = "longtext", SourceType = SourceType.Text, IsNullable = true },
            new ColumnDefinition { LogicalName = "flag", SourceType = SourceType.Boolean, IsNullable = true },
            new ColumnDefinition { LogicalName = "whole", SourceType = SourceType.Int32, IsNullable = true },
            new ColumnDefinition { LogicalName = "big", SourceType = SourceType.Int64, IsNullable = true },
            new ColumnDefinition { LogicalName = "amount", SourceType = SourceType.Decimal, Precision = 18, Scale = 4, IsNullable = true },
            new ColumnDefinition { LogicalName = "ratio", SourceType = SourceType.Double, IsNullable = true },
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
            new ColumnDefinition
            {
                LogicalName = "instant",
                SourceType = SourceType.DateTime,
                DateTimeBehavior = DateTimeBehavior.UserLocal,
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

    private static RepliceraConfiguration Configuration(string connectionVariable, string table) => new()
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
                Name = "postgres",
                Provider = "postgresql",
                ConnectionStringEnvironmentVariable = connectionVariable
            }
        ],
        Jobs =
        [
            new JobConfiguration
            {
                Name = "integration",
                Source = "source",
                Destination = "postgres",
                Tables = [table]
            }
        ]
    };

    private static SourcePage Page(params SourceRecord[] records) => new(records, null, null, false);

    private static SourceRecord Upsert(Guid id, string name, int status) => new(
        id,
        ChangeKind.Upsert,
        new Dictionary<string, object?> { ["name"] = name, ["statuscode"] = status });

    private static SourceRecord Delete(Guid id) => new(id, ChangeKind.Delete, new Dictionary<string, object?>());

    private sealed class TestDatabase(string adminConnectionString, string connectionString, string databaseName) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public static async Task<TestDatabase> CreateAsync()
        {
            var configured = IntegrationEnvironment.RequireConnectionString(ConnectionEnvironmentVariable);

            var adminBuilder = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres" };
            var databaseName = $"replicera_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
            await connection.OpenAsync(TestCancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE {PostgreSqlIdentifier.Quote(databaseName)};", connection);
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
            var databaseBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName };
            return new TestDatabase(adminBuilder.ConnectionString, databaseBuilder.ConnectionString, databaseName);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand($"DROP DATABASE {PostgreSqlIdentifier.Quote(databaseName)} WITH (FORCE);", connection);
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
