using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Core.Schema;
using Replicera.Provider.Oracle;

namespace Replicera.IntegrationTests;

public sealed class OracleProviderIntegrationTests
{
    private const string ConnectionEnvironmentVariable = "REPLICERA_ORACLE_TEST_CONNECTION_STRING";
    private static readonly CancellationToken TestCancellationToken = CancellationToken.None;

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task TableLockAndDependencyPreflight_ProtectSchemaLifecycle()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}");
        await PrepareTableAsync(connectionString, job, table);
        var provider = new OracleProvider();
        await using (var tableLock = await provider.AcquireTableLockAsync(connectionString, job, "account", TestCancellationToken))
        {
            await Assert.ThrowsAsync<Replicera.Core.Errors.SynchronizationAlreadyRunningException>(async () =>
                await provider.AcquireTableLockAsync(connectionString, job, "account", TestCancellationToken));
        }

        var viewName = $"account_view_{suffix}";
        await using (var connection = new OracleConnection(connectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE VIEW {OracleIdentifier.Quote(OracleIdentifier.Normalize(viewName))} AS SELECT ACCOUNTID FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))}";
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new OracleSchemaManager(connectionString);
        var current = await schema.ReadTableAsync(table, TestCancellationToken);
        var plan = SchemaPlanner.Plan(table, current, new SchemaPolicy(), SynchronizationMode.Reload);
        var error = await Assert.ThrowsAsync<RepliceraException>(() =>
            schema.ApplySchemaPlanAsync(job, table, plan, TestCancellationToken));
        Assert.Equal(ErrorCategory.SchemaConflict, error.Category);
        Assert.Contains(OracleIdentifier.Normalize(viewName), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task ConnectionProbe_ConnectsToConfiguredDatabase()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        await new OracleConnectionProbe(connectionString).TestConnectionAsync(TestCancellationToken);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task InitialAndIncrementalSync_CommitRowsMetricsAndCheckpoint()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}");
        await PrepareTableAsync(connectionString, job, table);
        var writer = new OracleDestinationWriter(connectionString);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync(job, table, TestCancellationToken))
        {
            Assert.Equal(new(2, 0, 0), await session.ApplyPageAsync(
                Page(Upsert(firstId, "First", 1), Upsert(secondId, "Second", 2)), TestCancellationToken));
            await session.CommitAsync("checkpoint-1", new(1, 2, 2, 0, 0), TestCancellationToken);
        }

        await using (var session = await writer.BeginIncrementalSyncAsync(
            job, table, "checkpoint-1", TestCancellationToken))
        {
            Assert.Equal(new(1, 1, 1), await session.ApplyPageAsync(
                Page(Upsert(firstId, "First updated", 3), Delete(secondId), Upsert(thirdId, "Third", 4)),
                TestCancellationToken));
            await session.CommitAsync("checkpoint-2", new(1, 3, 1, 1, 1), TestCancellationToken);
        }

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using (var rows = connection.CreateCommand())
        {
            rows.CommandText = $"SELECT NAME, STATUSCODE FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} ORDER BY NAME";
            await using var reader = await rows.ExecuteReaderAsync(TestCancellationToken);
            Assert.True(await reader.ReadAsync(TestCancellationToken));
            Assert.Equal("First updated", reader.GetString(0));
            Assert.Equal(3, reader.GetInt32(1));
            Assert.True(await reader.ReadAsync(TestCancellationToken));
            Assert.Equal("Third", reader.GetString(0));
            Assert.False(await reader.ReadAsync(TestCancellationToken));
        }

        var state = await new OracleReplicationStateStore(connectionString)
            .GetTableStateAsync(job, table.LogicalName, TestCancellationToken);
        Assert.NotNull(state);
        Assert.Equal(TableState.Healthy, state.State);
        Assert.Equal("checkpoint-2", state.DataCheckpoint);
        Assert.Equal(1, state.LastRunRecordsInserted);
        Assert.Equal(1, state.LastRunRecordsUpdated);
        Assert.Equal(1, state.LastRunRecordsDeleted);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task NoDataLossFullRead_RetainsRowsMissingFromSourceWithRemovalDate()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}");
        await PrepareTableAsync(connectionString, job, table, SynchronizationMode.NoDataLoss);
        var writer = new OracleDestinationWriter(connectionString);
        var keptId = Guid.NewGuid();
        var removedId = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync(
            job, table, TestCancellationToken, replaceExisting: false, retainDeletedRows: true, mode: SynchronizationMode.NoDataLoss))
        {
            _ = await session.ApplyPageAsync(
                Page(Upsert(keptId, "Kept", 1), Upsert(removedId, "Removed", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 2, 2, 0, 0), TestCancellationToken);
        }

        await using (var session = await writer.BeginInitialSyncAsync(
            job, table, TestCancellationToken, replaceExisting: false, retainDeletedRows: true, mode: SynchronizationMode.NoDataLoss))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(keptId, "Kept", 2)), TestCancellationToken);
            await session.CommitAsync("checkpoint-2", new(1, 1, 0, 1, 0), TestCancellationToken);
        }

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var rows = connection.CreateCommand();
        rows.CommandText = $"SELECT NAME, CASE WHEN DATE_SOURCE_REMOVE_DTE IS NULL THEN 0 ELSE 1 END FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} ORDER BY NAME";
        await using var reader = await rows.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Kept", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Removed", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.False(await reader.ReadAsync(TestCancellationToken));
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task LongStringColumns_ReplanCleanlyAndWidenIntoNclobWithoutLosingData()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var schema = new OracleSchemaManager(connectionString);

        var longTable = AccountsTable($"account_long_{suffix}", nameLength: 4_000);
        await PrepareTableAsync(connectionString, $"job_long_{suffix}", longTable);
        var replan = SchemaPlanner.Plan(longTable, await schema.ReadTableAsync(longTable, TestCancellationToken), new());
        Assert.False(replan.HasBlockingChanges);
        Assert.Empty(replan.Changes);

        var narrowTable = AccountsTable($"account_wide_{suffix}", nameLength: 1_000);
        var job = $"job_wide_{suffix}";
        await PrepareTableAsync(connectionString, job, narrowTable);
        var id = Guid.NewGuid();
        var writer = new OracleDestinationWriter(connectionString);
        await using (var session = await writer.BeginInitialSyncAsync(job, narrowTable, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(id, "Preserved", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        var widenedTable = AccountsTable(narrowTable.DestinationName, nameLength: 4_000);
        var widen = SchemaPlanner.Plan(widenedTable, await schema.ReadTableAsync(widenedTable, TestCancellationToken), new());
        Assert.Contains(widen.Changes, change => change.Kind == SchemaChangeKind.ExpandColumn && change.ObjectName == "name");
        await schema.ApplySchemaPlanAsync(job, widenedTable, widen, TestCancellationToken);
        Assert.Empty(SchemaPlanner.Plan(widenedTable, await schema.ReadTableAsync(widenedTable, TestCancellationToken), new()).Changes);

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using (var type = connection.CreateCommand())
        {
            type.CommandText = $"SELECT DATA_TYPE FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{OracleIdentifier.Normalize(widenedTable.DestinationName)}' AND COLUMN_NAME = 'NAME'";
            Assert.Equal("NCLOB", await type.ExecuteScalarAsync(TestCancellationToken));
        }

        await using (var rows = connection.CreateCommand())
        {
            rows.CommandText = $"SELECT NAME FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(widenedTable.DestinationName))}";
            Assert.Equal("Preserved", await rows.ExecuteScalarAsync(TestCancellationToken));
        }

        var state = await new OracleReplicationStateStore(connectionString)
            .GetTableStateAsync(job, widenedTable.LogicalName, TestCancellationToken);
        Assert.NotNull(state);
        Assert.Null(state.DataCheckpoint);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task RequiredSourceColumn_IsRelaxedAndAcceptsNullValues()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}", includeDescription: true);
        var tableName = OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName));
        await PrepareTableAsync(connectionString, job, table);
        await using (var connection = new OracleConnection(connectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = $"ALTER TABLE {tableName} MODIFY (\"DESCRIPTION\" NOT NULL)";
            _ = await legacy.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new OracleSchemaManager(connectionString);
        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync(table, TestCancellationToken), new());
        var relax = Assert.Single(plan.Changes);
        Assert.Equal(SchemaChangeKind.RelaxColumnNullability, relax.Kind);
        await schema.ApplySchemaPlanAsync(job, table, plan, TestCancellationToken);
        Assert.Empty(SchemaPlanner.Plan(table, await schema.ReadTableAsync(table, TestCancellationToken), new()).Changes);

        var writer = new OracleDestinationWriter(connectionString);
        await using (var session = await writer.BeginInitialSyncAsync(job, table, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "No description", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using var verify = new OracleConnection(connectionString);
        await verify.OpenAsync(TestCancellationToken);
        await using var command = verify.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE \"DESCRIPTION\" IS NULL";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(TestCancellationToken), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task DroppedManagedTable_IsRecreatedWithCheckpointClearedForFullRead()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}");
        await PrepareTableAsync(connectionString, job, table);
        var stateStore = new OracleReplicationStateStore(connectionString);
        var created = await stateStore.GetTableStateAsync(job, table.LogicalName, TestCancellationToken);
        Assert.NotEqual(TableState.ResyncRequired, created?.State);
        var writer = new OracleDestinationWriter(connectionString);
        await using (var session = await writer.BeginInitialSyncAsync(job, table, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "Existing", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using (var connection = new OracleConnection(connectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} PURGE";
            _ = await drop.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new OracleSchemaManager(connectionString);
        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync(table, TestCancellationToken), new());
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.CreateTable);
        await schema.ApplySchemaPlanAsync(job, table, plan, TestCancellationToken);

        var state = await stateStore.GetTableStateAsync(job, table.LogicalName, TestCancellationToken);
        Assert.NotNull(state);
        Assert.Null(state.DataCheckpoint);
        Assert.Equal(TableState.ResyncRequired, state.State);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task DecimalScaleIncrease_WidensColumnAndPreservesValues()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var destination = $"account_{suffix}";
        TableDefinition AmountTable(int scale) => new TableDefinition(
            "account",
            "accounts",
            destination,
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
        await PrepareTableAsync(connectionString, job, narrow);
        var schema = new OracleSchemaManager(connectionString);
        var writer = new OracleDestinationWriter(connectionString);
        var id = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync(job, narrow, TestCancellationToken))
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
        await schema.ApplySchemaPlanAsync(job, widened, plan, TestCancellationToken);

        var applied = await schema.ReadTableAsync(widened, TestCancellationToken);
        Assert.NotNull(applied);
        Assert.Equal(4, applied.Columns.Single(column => string.Equals(column.Name, "amount", StringComparison.OrdinalIgnoreCase)).Scale);
        Assert.Empty(SchemaPlanner.Plan(widened, applied, new SchemaPolicy()).Changes);

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT AMOUNT FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(destination))}";
        Assert.Equal(99999999999.25m, Convert.ToDecimal(await command.ExecuteScalarAsync(TestCancellationToken), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task ColumnThatBecomesUnsupported_IsRetainedWithExistingValues()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var destination = $"account_{suffix}";
        TableDefinition CodesTable(bool supported) => new TableDefinition(
            "account",
            "accounts",
            destination,
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
        await PrepareTableAsync(connectionString, job, CodesTable(true));
        var schema = new OracleSchemaManager(connectionString);
        var writer = new OracleDestinationWriter(connectionString);
        var id = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync(job, CodesTable(true), TestCancellationToken))
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
        await schema.ApplySchemaPlanAsync(job, unsupported, plan, TestCancellationToken);
        await using (var session = await writer.BeginIncrementalSyncAsync(job, unsupported, "checkpoint-1", TestCancellationToken))
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

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT NAME || '|' || LEGACYCODE FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(destination))}";
        Assert.Equal("Updated|KEEP", await command.ExecuteScalarAsync(TestCancellationToken));
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task RetainedLegacyColumn_IsRelaxedSoNewRowsCanBeInserted()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}");
        await PrepareTableAsync(connectionString, job, table, SynchronizationMode.NoDataLoss);
        var schema = new OracleSchemaManager(connectionString);
        await using (var connection = new OracleConnection(connectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = $"ALTER TABLE {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))} ADD (\"LEGACY\" NVARCHAR2(20) DEFAULT 'x' NOT NULL)";
            _ = await legacy.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var plan = SchemaPlanner.Plan(table, await schema.ReadTableAsync(table, TestCancellationToken), new SchemaPolicy(), SynchronizationMode.NoDataLoss);
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.SourceColumnRemoved && change.ObjectName.Equals("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.RelaxColumnNullability && change.ObjectName.Equals("legacy", StringComparison.OrdinalIgnoreCase));
        await schema.ApplySchemaPlanAsync(job, table, plan, TestCancellationToken);

        var writer = new OracleDestinationWriter(connectionString);
        await using (var session = await writer.BeginInitialSyncAsync(
            job, table, TestCancellationToken, replaceExisting: false, retainDeletedRows: true, mode: SynchronizationMode.NoDataLoss))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "New row", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }
        var relaxed = await schema.ReadTableAsync(table, TestCancellationToken);
        Assert.True(relaxed!.Columns.Single(column => column.Name == "LEGACY").IsNullable);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task NarrowerSourceColumn_KeepsWiderDestinationAndContinuesSyncing()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var destinationName = $"account_{suffix}";
        await PrepareTableAsync(connectionString, job, AccountsTable(destinationName, nameLength: 200));
        var schema = new OracleSchemaManager(connectionString);
        var writer = new OracleDestinationWriter(connectionString);
        var narrowed = AccountsTable(destinationName, nameLength: 100);
        var plan = SchemaPlanner.Plan(narrowed, await schema.ReadTableAsync(narrowed, TestCancellationToken), new SchemaPolicy());
        Assert.Equal(SchemaChangeKind.NarrowerSourceColumn, Assert.Single(plan.Changes).Kind);
        await schema.ApplySchemaPlanAsync(job, narrowed, plan, TestCancellationToken);
        await using (var session = await writer.BeginInitialSyncAsync(job, narrowed, TestCancellationToken))
        {
            _ = await session.ApplyPageAsync(Page(Upsert(Guid.NewGuid(), "Short", 1)), TestCancellationToken);
            await session.CommitAsync("checkpoint-1", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        var destination = await schema.ReadTableAsync(narrowed, TestCancellationToken);
        Assert.Equal(200, destination!.Columns.Single(column => string.Equals(column.Name, "name", StringComparison.OrdinalIgnoreCase)).MaxLength);
    }

    [Theory]
    [Trait("Category", "OracleIntegration")]
    [InlineData(SynchronizationMode.Complete)]
    [InlineData(SynchronizationMode.NoDataLoss)]
    public async Task ColumnWithChangedType_IsReplacedAccordingToMode(SynchronizationMode mode)
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var destinationName = $"account_{suffix}";
        TableDefinition CodeTable(bool numeric) => new TableDefinition(
            "account",
            "accounts",
            destinationName,
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, IsNullable = true, MaxLength = 100 },
                numeric
                    ? new ColumnDefinition { LogicalName = "code", SourceType = SourceType.Int32, IsNullable = true }
                    : new ColumnDefinition { LogicalName = "code", SourceType = SourceType.String, IsNullable = true, MaxLength = 20 }
            ]);
        await PrepareTableAsync(connectionString, job, CodeTable(false), mode);
        var schema = new OracleSchemaManager(connectionString);
        var writer = new OracleDestinationWriter(connectionString);
        var stateStore = new OracleReplicationStateStore(connectionString);
        var noDataLoss = mode == SynchronizationMode.NoDataLoss;
        var id = Guid.NewGuid();
        await using (var session = await writer.BeginInitialSyncAsync(
            job, CodeTable(false), TestCancellationToken, replaceExisting: !noDataLoss, retainDeletedRows: noDataLoss, mode: mode))
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
        await schema.ApplySchemaPlanAsync(job, replaced, plan, TestCancellationToken);

        var state = await stateStore.GetTableStateAsync(job, "account", TestCancellationToken);
        Assert.Null(state!.DataCheckpoint);
        var destination = await schema.ReadTableAsync(replaced, TestCancellationToken);
        Assert.Equal(SourceType.Int32, destination!.Columns.Single(column => string.Equals(column.Name, "code", StringComparison.OrdinalIgnoreCase)).SourceType);
        Assert.Equal(noDataLoss, destination.Columns.Any(column => string.Equals(column.Name, "code_replaced_20260930", StringComparison.OrdinalIgnoreCase)));

        await using (var session = await writer.BeginInitialSyncAsync(
            job, replaced, TestCancellationToken, replaceExisting: !noDataLoss, retainDeletedRows: noDataLoss, mode: mode))
        {
            _ = await session.ApplyPageAsync(
                new SourcePage([new SourceRecord(id, ChangeKind.Upsert, new Dictionary<string, object?> { ["name"] = "Row", ["code"] = 42 })], null, null, false),
                TestCancellationToken);
            await session.CommitAsync("checkpoint-2", new(1, 1, 0, 1, 0), TestCancellationToken);
        }

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        var tableName = OracleIdentifier.Quote(OracleIdentifier.Normalize(destinationName));
        command.CommandText = noDataLoss
            ? $"SELECT CODE || '|' || CODE_REPLACED_20260930 FROM {tableName}"
            : $"SELECT CODE || '|' FROM {tableName}";
        Assert.Equal(noDataLoss ? "42|A-1" : "42|", await command.ExecuteScalarAsync(TestCancellationToken));
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task InterruptedAndConcurrentSessions_PreserveCommittedStateAndEnforceLock()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}");
        await PrepareTableAsync(connectionString, job, table);
        var writer = new OracleDestinationWriter(connectionString);
        var id = Guid.NewGuid();
        await using (var initial = await writer.BeginInitialSyncAsync(job, table, TestCancellationToken))
        {
            _ = await initial.ApplyPageAsync(Page(Upsert(id, "Original", 1)), TestCancellationToken);
            await initial.CommitAsync("stable", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using (var interrupted = await writer.BeginIncrementalSyncAsync(job, table, "stable", TestCancellationToken))
        {
            _ = await interrupted.ApplyPageAsync(Page(Upsert(id, "Uncommitted", 2)), TestCancellationToken);
        }

        await using (var first = await writer.BeginIncrementalSyncAsync(job, table, "stable", TestCancellationToken))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.BeginIncrementalSyncAsync(job, table, "stable", TestCancellationToken));
            Assert.Contains("already running", error.Message, StringComparison.Ordinal);
        }

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT NAME FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))}";
        Assert.Equal("Original", await command.ExecuteScalarAsync(TestCancellationToken));
        var state = await new OracleReplicationStateStore(connectionString)
            .GetTableStateAsync(job, table.LogicalName, TestCancellationToken);
        Assert.Equal("stable", state?.DataCheckpoint);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task SchemaManager_RejectsUnmanagedTableAndAppliesSafeExpansion()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var unmanaged = AccountsTable($"unmanaged_{suffix}", 20);
        await using (var connection = new OracleConnection(connectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var create = connection.CreateCommand();
            create.CommandText = OracleDdlBuilder.BuildCreateTable(unmanaged);
            _ = await create.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var unmanagedState = await new OracleSchemaManager(connectionString)
            .ReadTableAsync(unmanaged, TestCancellationToken);
        Assert.NotNull(unmanagedState);
        Assert.False(unmanagedState.IsManaged);
        Assert.Equal(
            SchemaChangeKind.OwnershipConflict,
            Assert.Single(SchemaPlanner.Plan(unmanaged, unmanagedState, new()).Changes).Kind);

        var job = $"job_{suffix}";
        var initial = AccountsTable($"managed_{suffix}", 20);
        await PrepareTableAsync(connectionString, job, initial);
        var expanded = AccountsTable(initial.DestinationName, 400, true);
        var schema = new OracleSchemaManager(connectionString);
        var plan = SchemaPlanner.Plan(
            expanded,
            await schema.ReadTableAsync(expanded, TestCancellationToken),
            new());
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.ExpandColumn);
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.AddColumn);
        await schema.ApplySchemaPlanAsync(job, expanded, plan, TestCancellationToken);
        var applied = await schema.ReadTableAsync(expanded, TestCancellationToken);
        Assert.NotNull(applied);
        Assert.Equal(400, applied.Columns.Single(column => column.Name == "NAME").MaxLength);
        Assert.Contains(applied.Columns, column => column.Name == "DESCRIPTION" && column.IsNullable);

        var contracted = AccountsTable(initial.DestinationName, 400);
        var dropPlan = SchemaPlanner.Plan(contracted, applied, new SchemaPolicy());
        Assert.Contains(dropPlan.Changes, change => change.Kind == SchemaChangeKind.DropColumn);
        await schema.ApplySchemaPlanAsync(job, contracted, dropPlan, TestCancellationToken);
        var contractedDestination = await schema.ReadTableAsync(contracted, TestCancellationToken);
        Assert.NotNull(contractedDestination);
        Assert.DoesNotContain(contractedDestination.Columns, column => column.Name == "DESCRIPTION");
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task FailureState_IsDurableAndDoesNotExposeCheckpoint()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = AccountsTable($"account_{suffix}");
        await PrepareTableAsync(connectionString, job, table);
        var store = new OracleReplicationStateStore(connectionString);
        await store.MarkFailureAsync(
            job,
            table.LogicalName,
            TableState.Failed,
            "Synchronization",
            "Synchronization failed before the checkpoint could be committed.",
            TestCancellationToken);

        var state = await store.GetTableStateAsync(job, table.LogicalName, TestCancellationToken);
        Assert.NotNull(state);
        Assert.Equal(TableState.Failed, state.State);
        Assert.Equal("Synchronization", state.LastErrorCode);
        Assert.Null(state.DataCheckpoint);
    }

    [Fact]
    [Trait("Category", "OracleIntegration")]
    public async Task SupportedValues_RoundTripThroughArrayBinding()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
        {
            return;
        }

        var suffix = UniqueSuffix();
        var job = $"job_{suffix}";
        var table = FidelityTable($"fidelity_{suffix}");
        await PrepareTableAsync(connectionString, job, table);
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
        await using (var session = await new OracleDestinationWriter(connectionString)
            .BeginInitialSyncAsync(job, table, TestCancellationToken))
        {
            Assert.Equal(1, (await session.ApplyPageAsync(Page(record), TestCancellationToken)).Inserted);
            await session.CommitAsync("fidelity", new(1, 1, 1, 0, 0), TestCancellationToken);
        }

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT LONGTEXT, FLAG, WHOLE, BIG, AMOUNT, RATIO,
                   TO_CHAR(SAMPLEDATE, 'YYYY-MM-DD'),
                   TO_CHAR(MOMENT, 'YYYY-MM-DD HH24:MI:SS'),
                   TO_CHAR(SYS_EXTRACT_UTC(INSTANT), 'YYYY-MM-DD HH24:MI:SS'),
                   CATEGORIES, RAWTOHEX(OWNERID), OWNERID_TYPE
            FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(table.DestinationName))}
            """;
        await using var reader = await command.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await reader.ReadAsync(TestCancellationToken));
        Assert.Equal("Unicode ✓", reader.GetString(0));
        Assert.Equal(1, reader.GetInt16(1));
        Assert.Equal(int.MaxValue, reader.GetInt32(2));
        Assert.Equal(long.MaxValue, reader.GetInt64(3));
        Assert.Equal(123456789.1234m, reader.GetDecimal(4));
        Assert.Equal(1.0d / 3.0d, reader.GetDouble(5), 12);
        Assert.Equal("2026-09-20", reader.GetString(6));
        Assert.Equal("2026-09-20 12:34:56", reader.GetString(7));
        Assert.Equal("2026-09-20 18:34:56", reader.GetString(8));
        Assert.Equal("[1,3]", reader.GetString(9));
        Assert.Equal(Convert.ToHexString(ownerId.ToByteArray(bigEndian: true)), reader.GetString(10));
        Assert.Equal("team", reader.GetString(11));
    }

    private static string? ConnectionString() =>
        Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

    private static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];

    private static async Task PrepareTableAsync(
        string connectionString,
        string job,
        TableDefinition table,
        SynchronizationMode mode = SynchronizationMode.Complete)
    {
        await new OracleMetadataStore(connectionString).EnsureCreatedAsync(TestCancellationToken);
        var schema = new OracleSchemaManager(connectionString);
        var plan = SchemaPlanner.Plan(
            table,
            await schema.ReadTableAsync(table, TestCancellationToken),
            new(),
            mode);
        await schema.ApplySchemaPlanAsync(job, table, plan, TestCancellationToken);
    }

    private static TableDefinition AccountsTable(
        string destinationName,
        int nameLength = 200,
        bool includeDescription = false) => new(
        "account",
        "accounts",
        destinationName,
        new List<ColumnDefinition>
        {
            new() { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new() { LogicalName = "name", SourceType = SourceType.String, IsNullable = true, MaxLength = nameLength },
            new() { LogicalName = "statuscode", SourceType = SourceType.Choice, IsNullable = true }
        }.Concat(includeDescription
            ? [new ColumnDefinition { LogicalName = "description", SourceType = SourceType.String, IsNullable = false, MaxLength = 1000 }]
            : []));

    private static TableDefinition FidelityTable(string destinationName) => new(
        "fidelity",
        "fidelities",
        destinationName,
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

    private static SourcePage Page(params SourceRecord[] records) => new(records, null, null, false);

    private static SourceRecord Upsert(Guid id, string name, int status) => new(
        id,
        ChangeKind.Upsert,
        new Dictionary<string, object?> { ["name"] = name, ["statuscode"] = status });

    private static SourceRecord Delete(Guid id) =>
        new(id, ChangeKind.Delete, new Dictionary<string, object?>());
}
