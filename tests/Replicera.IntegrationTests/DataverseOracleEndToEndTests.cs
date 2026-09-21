using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Configuration;
using Replicera.Core.Models;
using Replicera.Core.Replication;
using Replicera.Core.Schema;
using Replicera.Dataverse;
using Replicera.Dataverse.Authentication;
using Replicera.Dataverse.ChangeTracking;
using Replicera.Provider.Oracle;

namespace Replicera.IntegrationTests;

public sealed class DataverseOracleEndToEndTests
{
    [Fact]
    [Trait("Category", "DataverseOracleEndToEnd")]
    public async Task AccountLifecycle_IsMirroredIntoOracle()
    {
        var connectionString = GetRequiredEnvironmentVariable("REPLICERA_ORACLE_TEST_CONNECTION_STRING");
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);
        await source.TestConnectionAsync(CancellationToken.None);
        var sourceTable = await source.GetTableAsync("account", CancellationToken.None);
        Assert.True(sourceTable.PrimaryKey.IsSupported, sourceTable.PrimaryKey.UnsupportedReason);
        var tracking = await new DataverseChangeTrackingManager(service).GetStatusAsync(
            sourceTable.LogicalName,
            CancellationToken.None);
        Assert.True(tracking.IsEnabled);

        var destinationName = $"account_e2e_{Guid.NewGuid():N}";
        var table = new TableDefinition(
            sourceTable.LogicalName,
            sourceTable.EntitySetName,
            destinationName,
            sourceTable.Columns);
        var jobName = $"e2e_{Guid.NewGuid():N}";
        await new OracleMetadataStore(connectionString).EnsureCreatedAsync(CancellationToken.None);
        var schema = new OracleSchemaManager(connectionString);
        var plan = SchemaPlanner.Plan(
            table,
            await schema.ReadTableAsync(table, CancellationToken.None),
            new SchemaPolicy());
        Assert.False(plan.HasBlockingChanges);
        await schema.ApplySchemaPlanAsync(jobName, table, plan, CancellationToken.None);

        Guid? accountId = null;
        var deleted = false;
        try
        {
            var initialName = $"Replicera Oracle end-to-end {Guid.NewGuid():N}";
            accountId = await CreateAccountAsync(service, initialName);
            var engine = new ReplicationEngine(
                new DataverseChangeReader(service),
                new OracleDestinationWriter(connectionString),
                new OracleReplicationStateStore(connectionString));

            var initialMetrics = await engine.SyncAsync(
                jobName,
                table,
                5_000,
                CancellationToken.None);
            Assert.True(initialMetrics.RecordsReceived >= 1);
            Assert.Equal(initialName, await ReadAccountNameAsync(connectionString, destinationName, accountId.Value));

            var updatedName = $"Replicera Oracle end-to-end updated {Guid.NewGuid():N}";
            await service.ExecuteAsync(
                new UpdateRequest
                {
                    Target = new Entity("account", accountId.Value) { ["name"] = updatedName }
                },
                CancellationToken.None);
            _ = await engine.SyncAsync(jobName, table, 5_000, CancellationToken.None);
            Assert.Equal(updatedName, await ReadAccountNameAsync(connectionString, destinationName, accountId.Value));

            await service.ExecuteAsync(
                new DeleteRequest { Target = new EntityReference("account", accountId.Value) },
                CancellationToken.None);
            deleted = true;
            _ = await engine.SyncAsync(jobName, table, 5_000, CancellationToken.None);
            Assert.Null(await ReadAccountNameAsync(connectionString, destinationName, accountId.Value));
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

    private static async Task<string?> ReadAccountNameAsync(
        string connectionString,
        string tableName,
        Guid accountId)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
#pragma warning disable CA2100 // The provider normalizes and quotes the generated test table name.
        command.CommandText = $"SELECT NAME FROM {OracleIdentifier.Quote(OracleIdentifier.Normalize(tableName))} WHERE ACCOUNTID = :id";
#pragma warning restore CA2100
        command.Parameters.Add("id", OracleDbType.Raw, 16).Value = accountId.ToByteArray(bigEndian: true);
        return await command.ExecuteScalarAsync(CancellationToken.None) as string;
    }

    private static SourceConfiguration SourceConfiguration() => new()
    {
        Name = "oracle-end-to-end",
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
}
