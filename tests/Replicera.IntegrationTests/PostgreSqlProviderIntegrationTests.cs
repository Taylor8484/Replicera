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

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task TableLockAndDependencyPreflight_ProtectSchemaLifecycle()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var provider = new PostgreSqlProvider();
        await using (var tableLock = await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider.AcquireTableLockAsync(database.ConnectionString, "integration", "account", TestCancellationToken));
        }

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = new NpgsqlCommand("CREATE VIEW public.account_view AS SELECT accountid FROM public.account;", connection);
            _ = await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var schema = new PostgreSqlSchemaManager(database.ConnectionString);
        var current = await schema.ReadTableAsync(table, TestCancellationToken);
        var plan = SchemaPlanner.Plan(table, current, new SchemaPolicy(), SynchronizationMode.Reload);
        var error = await Assert.ThrowsAsync<RepliceraException>(() =>
            schema.ApplySchemaPlanAsync("integration", table, plan, TestCancellationToken));
        Assert.Equal(ErrorCategory.SchemaConflict, error.Category);
        Assert.Contains("account_view", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task StateStore_BeforeFirstSyncReturnsUninitializedState()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        Assert.Null(await new PostgreSqlReplicationStateStore(database.ConnectionString)
            .GetTableStateAsync("integration", "account", TestCancellationToken));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task InitialAndIncrementalSync_CommitRowsMetricsAndCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

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

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DisposedSession_RollsBackRowsCheckpointAndTemporaryStaging()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

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

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ConcurrentSession_ForSameJobAndTableIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        var table = AccountsTable();
        await PrepareTableAsync(database.ConnectionString, table);
        var writer = new PostgreSqlDestinationWriter(database.ConnectionString);
        await using var first = await writer.BeginInitialSyncAsync("integration", table, TestCancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.BeginInitialSyncAsync("integration", table, TestCancellationToken));

        Assert.Contains("already running", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task FailureState_IsDurableAndSanitized()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

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

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SchemaManager_RejectsUnmanagedTableAndAppliesSafeExpansion()
    {
        await using var unmanagedDatabase = await TestDatabase.CreateAsync();
        if (unmanagedDatabase is null)
        {
            return;
        }

        await using (var connection = new NpgsqlConnection(unmanagedDatabase.ConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var create = new NpgsqlCommand(
                "CREATE TABLE public.account (accountid uuid PRIMARY KEY, name character varying(20), statuscode integer);",
                connection);
            _ = await create.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var unmanaged = await new PostgreSqlSchemaManager(unmanagedDatabase.ConnectionString)
            .ReadTableAsync(AccountsTable(20), TestCancellationToken);
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
        var plan = SchemaPlanner.Plan(
            expanded,
            await schema.ReadTableAsync(expanded, TestCancellationToken),
            new SchemaPolicy());
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.ExpandColumn);
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.AddColumn);
        await schema.ApplySchemaPlanAsync("integration", expanded, plan, TestCancellationToken);
        var applied = await schema.ReadTableAsync(expanded, TestCancellationToken);
        Assert.NotNull(applied);
        Assert.Equal(400, applied.Columns.Single(column => column.Name == "name").MaxLength);
        Assert.Contains(applied.Columns, column => column.Name == "description" && column.IsNullable);

        var contracted = AccountsTable(400);
        var dropPlan = SchemaPlanner.Plan(contracted, applied, new SchemaPolicy());
        Assert.Contains(dropPlan.Changes, change => change.Kind == SchemaChangeKind.DropColumn);
        await schema.ApplySchemaPlanAsync("integration", contracted, dropPlan, TestCancellationToken);
        var contractedDestination = await schema.ReadTableAsync(contracted, TestCancellationToken);
        Assert.NotNull(contractedDestination);
        Assert.DoesNotContain(contractedDestination.Columns, column => column.Name == "description");
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SupportedValues_RoundTripAndStatusUsesConfiguredProvider()
    {
        await using var database = await TestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

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
            await schema.ReadTableAsync(table, TestCancellationToken),
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

        public static async Task<TestDatabase?> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

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
