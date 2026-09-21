using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Replicera.Core.Configuration;
using Replicera.Core.Replication;
using Replicera.Core.Schema;
using Replicera.Dataverse;
using Replicera.Dataverse.Authentication;
using Replicera.Dataverse.ChangeTracking;
using Replicera.Provider.SqlServer;

namespace Replicera.IntegrationTests;

public sealed class DataverseSqlEndToEndTests
{
    [Fact]
    [Trait("Category", "DataverseSqlEndToEnd")]
    public async Task AccountLifecycle_IsMirroredIntoSqlServer()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);
        await source.TestConnectionAsync(CancellationToken.None);
        var table = await source.GetTableAsync("account", CancellationToken.None);
        Assert.True(table.PrimaryKey.IsSupported, table.PrimaryKey.UnsupportedReason);
        var tracking = await new DataverseChangeTrackingManager(service).GetStatusAsync(
            table.LogicalName,
            CancellationToken.None);
        Assert.True(tracking.IsEnabled);

        var metadata = new SqlServerMetadataStore(database.ConnectionString);
        await metadata.EnsureCreatedAsync(CancellationToken.None);
        var schema = new SqlServerSchemaManager(database.ConnectionString);
        var current = await schema.ReadTableAsync(table, CancellationToken.None);
        var plan = SchemaPlanner.Plan(table, current, new SchemaPolicy());
        Assert.False(plan.HasBlockingChanges);
        await schema.ApplySchemaPlanAsync("end-to-end", table, plan, CancellationToken.None);

        Guid? accountId = null;
        var deleted = false;
        try
        {
            var initialName = $"Replicera end-to-end {Guid.NewGuid():N}";
            accountId = await CreateAccountAsync(service, initialName);
            var engine = new ReplicationEngine(
                new DataverseChangeReader(service),
                new SqlServerDestinationWriter(database.ConnectionString),
                new SqlServerReplicationStateStore(database.ConnectionString));

            var initialMetrics = await engine.SyncAsync(
                "end-to-end",
                table,
                5_000,
                CancellationToken.None);
            Assert.True(initialMetrics.RecordsReceived >= 1);
            Assert.Equal(initialName, await ReadAccountNameAsync(database.ConnectionString, accountId.Value));

            var updatedName = $"Replicera end-to-end updated {Guid.NewGuid():N}";
            await service.ExecuteAsync(
                new UpdateRequest
                {
                    Target = new Entity("account", accountId.Value) { ["name"] = updatedName }
                },
                CancellationToken.None);
            _ = await engine.SyncAsync("end-to-end", table, 5_000, CancellationToken.None);
            Assert.Equal(updatedName, await ReadAccountNameAsync(database.ConnectionString, accountId.Value));

            await service.ExecuteAsync(
                new DeleteRequest { Target = new EntityReference("account", accountId.Value) },
                CancellationToken.None);
            deleted = true;
            _ = await engine.SyncAsync("end-to-end", table, 5_000, CancellationToken.None);
            Assert.Null(await ReadAccountNameAsync(database.ConnectionString, accountId.Value));
        }
        finally
        {
            if (accountId is not null && !deleted)
            {
                try
                {
                    await service.ExecuteAsync(
                        new DeleteRequest { Target = new EntityReference("account", accountId.Value) },
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original test failure; the generated name allows manual cleanup.
                }
            }
        }
    }

    private static async Task<Guid> CreateAccountAsync(DataverseService service, string name)
    {
        var response = (CreateResponse)await service.ExecuteAsync(
            new CreateRequest { Target = new Entity("account") { ["name"] = name } },
            CancellationToken.None);
        return response.id;
    }

    private static async Task<string?> ReadAccountNameAsync(string connectionString, Guid accountId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [name] FROM [dbo].[account] WHERE [accountid] = @id;";
        _ = command.Parameters.AddWithValue("@id", accountId);
        return await command.ExecuteScalarAsync(CancellationToken.None) as string;
    }

    private static SourceConfiguration SourceConfiguration() => new()
    {
        Name = "end-to-end",
        Url = new Uri(GetRequiredEnvironmentVariable("REPLICERA_DATAVERSE_URL")),
        TenantId = Guid.Parse(GetRequiredEnvironmentVariable("REPLICERA_DATAVERSE_TENANT_ID")),
        ClientId = Guid.Parse(GetRequiredEnvironmentVariable("REPLICERA_DATAVERSE_CLIENT_ID")),
        Authentication = new AuthenticationConfiguration
        {
            Method = AuthenticationMethod.ClientSecret,
            SecretEnvironmentVariable = "REPLICERA_DATAVERSE_CLIENT_SECRET"
        }
    };

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable '{name}' is required for end-to-end tests.");

    private sealed class TestDatabase(string adminConnectionString, string connectionString, string databaseName) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public static async Task<TestDatabase> CreateAsync()
        {
            var configured = GetRequiredEnvironmentVariable("REPLICERA_SQL_TEST_CONNECTION_STRING");
            var adminBuilder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
            var databaseName = $"replicera_e2e_{Guid.NewGuid():N}";
            await using var connection = new SqlConnection(adminBuilder.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}];";
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
            var databaseBuilder = new SqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                InitialCatalog = databaseName
            };
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
