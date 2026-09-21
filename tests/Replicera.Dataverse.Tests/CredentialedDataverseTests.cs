using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Dataverse.Authentication;
using Replicera.Dataverse.ChangeTracking;
using Replicera.Dataverse.Security;
using Xunit.Abstractions;

namespace Replicera.Dataverse.Tests;

public sealed class CredentialedDataverseTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task ApplicationUser_HasSteadyStatePumpRolesWithoutSystemAdministrator()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var who = (WhoAmIResponse)await service.ExecuteAsync(
            new WhoAmIRequest(), CancellationToken.None);
        var query = new QueryExpression("role") { ColumnSet = new ColumnSet("name", "roletemplateid") };
        var link = query.AddLink("systemuserroles", "roleid", "roleid");
        link.LinkCriteria.AddCondition("systemuserid", ConditionOperator.Equal, who.UserId);
        var response = (RetrieveMultipleResponse)await service.ExecuteAsync(
            new RetrieveMultipleRequest { Query = query }, CancellationToken.None);
        var roles = response.EntityCollection.Entities
            .Select(role => role.GetAttributeValue<string>("name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roleTemplates = response.EntityCollection.Entities
            .Select(role => role.GetAttributeValue<EntityReference>("roletemplateid")?.Id)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        Assert.Contains("Replicera Pump Reader", roles);
        Assert.Contains(DataversePermissionBootstrapper.SystemCustomizerRoleTemplateId, roleTemplates);
        Assert.DoesNotContain(DataversePermissionBootstrapper.SystemAdministratorRoleTemplateId, roleTemplates);
    }

    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task ClientSecret_ConnectsAndReadsAccountMetadata()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);

        await source.TestConnectionAsync(CancellationToken.None);
        var table = await source.GetTableAsync("account", CancellationToken.None);

        Assert.Equal("account", table.LogicalName);
        Assert.NotEmpty(table.Columns);
        Assert.True(table.PrimaryKey.IsPrimaryKey);
    }

    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task AccountChangeTrackingStatus_CanBeRead()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var status = await new DataverseChangeTrackingManager(service).GetStatusAsync(
            "account",
            CancellationToken.None);

        output.WriteLine($"account change tracking enabled: {status.IsEnabled}; can enable: {status.CanEnable}");
        Assert.True(status.IsEnabled || status.CanEnable, status.BlockedReason);
    }

    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task AccountChangeTrackingMetadata_CanBeUpdatedWithoutChangingSetting()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var retrieveResponse = (RetrieveEntityResponse)await service.ExecuteAsync(
            new RetrieveEntityRequest
            {
                LogicalName = "account",
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            },
            CancellationToken.None);

        Assert.True(retrieveResponse.EntityMetadata.ChangeTrackingEnabled);
        retrieveResponse.EntityMetadata.ChangeTrackingEnabled = true;
        _ = await service.ExecuteAsync(
            new UpdateEntityRequest { Entity = retrieveResponse.EntityMetadata },
            CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task AccountChangeFeed_ReturnsTerminalCheckpointAndAcceptsItIncrementally()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);
        var table = await source.GetTableAsync("account", CancellationToken.None);
        var reader = new DataverseChangeReader(service);

        var initialPages = 0;
        var initialRecords = 0;
        string? checkpoint = null;
        await foreach (var page in reader.ReadChangesAsync(table, null, 5_000, CancellationToken.None))
        {
            initialPages++;
            initialRecords += page.Records.Count;
            if (page.HasMoreRecords)
            {
                Assert.Null(page.DataCheckpoint);
            }
            else
            {
                checkpoint = page.DataCheckpoint;
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(checkpoint));
        var incrementalPages = 0;
        string? nextCheckpoint = null;
        await foreach (var page in reader.ReadChangesAsync(table, checkpoint, 5_000, CancellationToken.None))
        {
            incrementalPages++;
            if (!page.HasMoreRecords)
            {
                nextCheckpoint = page.DataCheckpoint;
            }
        }

        output.WriteLine(
            $"account initial feed: {initialPages} page(s), {initialRecords} record(s); " +
            $"incremental confirmation: {incrementalPages} page(s).");
        Assert.False(string.IsNullOrWhiteSpace(nextCheckpoint));
    }

    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task InvalidCheckpoint_IsClassifiedForFullResynchronization()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);
        var table = await source.GetTableAsync("account", CancellationToken.None);
        var reader = new DataverseChangeReader(service);
        const string invalidCheckpoint = "replicera-deliberately-invalid-checkpoint";

        var exception = await Assert.ThrowsAsync<RepliceraException>(async () =>
        {
            await foreach (var _ in reader.ReadChangesAsync(
                               table,
                               invalidCheckpoint,
                               5_000,
                               CancellationToken.None))
            {
            }
        });

        Assert.Equal(ErrorCategory.ExpiredCheckpoint, exception.Category);
        Assert.Equal(ExitCode.ResynchronizationRequired, ExitCodeMapper.From(exception.Category));
        Assert.DoesNotContain(invalidCheckpoint, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task AccountLifecycle_ProducesUpsertUpdateAndDeleteChanges()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);
        var table = await source.GetTableAsync("account", CancellationToken.None);
        var reader = new DataverseChangeReader(service);
        var (_, checkpoint) = await ReadFeedAsync(reader, table, null);
        Guid? accountId = null;
        var deleted = false;
        try
        {
            var createResponse = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest
                {
                    Target = new Entity("account")
                    {
                        ["name"] = $"Replicera credentialed test {Guid.NewGuid():N}"
                    }
                },
                CancellationToken.None);
            accountId = createResponse.id;

            var (createdChanges, afterCreate) = await ReadFeedAsync(reader, table, checkpoint);
            Assert.Contains(createdChanges, record => record.Id == accountId && record.Kind == ChangeKind.Upsert);

            await service.ExecuteAsync(
                new UpdateRequest
                {
                    Target = new Entity("account", accountId.Value)
                    {
                        ["name"] = $"Replicera credentialed test updated {Guid.NewGuid():N}"
                    }
                },
                CancellationToken.None);
            var (updatedChanges, afterUpdate) = await ReadFeedAsync(reader, table, afterCreate);
            Assert.Contains(updatedChanges, record => record.Id == accountId && record.Kind == ChangeKind.Upsert);

            await service.ExecuteAsync(
                new DeleteRequest { Target = new EntityReference("account", accountId.Value) },
                CancellationToken.None);
            deleted = true;
            var (deletedChanges, _) = await ReadFeedAsync(reader, table, afterUpdate);
            Assert.Contains(deletedChanges, record => record.Id == accountId && record.Kind == ChangeKind.Delete);
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
                    // Preserve the original test failure; the unique name allows manual cleanup.
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Credentialed")]
    public async Task InitialFeed_WithConcurrentMutations_ProducesCurrentState()
    {
        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);
        var table = await source.GetTableAsync("account", CancellationToken.None);
        var reader = new DataverseChangeReader(service);
        var createdIds = new List<Guid>();
        try
        {
            createdIds.Add(await CreateAccountAsync(service, "initial-one"));
            createdIds.Add(await CreateAccountAsync(service, "initial-two"));
            createdIds.Add(await CreateAccountAsync(service, "initial-three"));
            var mirroredIds = new HashSet<Guid>();
            string? terminalCheckpoint = null;
            await using (var pages = reader.ReadChangesAsync(table, null, 1, CancellationToken.None).GetAsyncEnumerator())
            {
                Assert.True(await pages.MoveNextAsync());
                Assert.True(pages.Current.HasMoreRecords);
                foreach (var record in pages.Current.Records)
                {
                    ApplyChange(mirroredIds, record);
                }

                var concurrentId = await CreateAccountAsync(service, "concurrent");
                createdIds.Add(concurrentId);
                await service.ExecuteAsync(
                    new UpdateRequest
                    {
                        Target = new Entity("account", createdIds[0])
                        {
                            ["name"] = $"Replicera credentialed concurrent update {Guid.NewGuid():N}"
                        }
                    },
                    CancellationToken.None);
                await service.ExecuteAsync(
                    new DeleteRequest { Target = new EntityReference("account", createdIds[1]) },
                    CancellationToken.None);
                while (await pages.MoveNextAsync())
                {
                    foreach (var record in pages.Current.Records)
                    {
                        ApplyChange(mirroredIds, record);
                    }

                    if (!pages.Current.HasMoreRecords)
                    {
                        terminalCheckpoint = pages.Current.DataCheckpoint;
                    }
                }

                Assert.False(string.IsNullOrWhiteSpace(terminalCheckpoint));
                var (incrementalChanges, _) = await ReadFeedAsync(reader, table, terminalCheckpoint);
                foreach (var record in incrementalChanges)
                {
                    ApplyChange(mirroredIds, record);
                }

                Assert.Contains(createdIds[0], mirroredIds);
                Assert.DoesNotContain(createdIds[1], mirroredIds);
                Assert.Contains(createdIds[2], mirroredIds);
                Assert.Contains(concurrentId, mirroredIds);
            }
        }
        finally
        {
            foreach (var id in createdIds)
            {
                try
                {
                    await service.ExecuteAsync(
                        new DeleteRequest { Target = new EntityReference("account", id) },
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original test failure; generated names allow manual cleanup.
                }
            }
        }
    }

    private static void ApplyChange(HashSet<Guid> mirroredIds, SourceRecord record)
    {
        if (record.Kind == ChangeKind.Delete)
        {
            _ = mirroredIds.Remove(record.Id);
        }
        else
        {
            _ = mirroredIds.Add(record.Id);
        }
    }

    private static async Task<Guid> CreateAccountAsync(DataverseService service, string suffix)
    {
        var response = (CreateResponse)await service.ExecuteAsync(
            new CreateRequest
            {
                Target = new Entity("account")
                {
                    ["name"] = $"Replicera credentialed {suffix} {Guid.NewGuid():N}"
                }
            },
            CancellationToken.None);
        return response.id;
    }

    private static async Task<(IReadOnlyList<SourceRecord> Records, string Checkpoint)> ReadFeedAsync(
        DataverseChangeReader reader,
        TableDefinition table,
        string? checkpoint)
    {
        var records = new List<SourceRecord>();
        string? terminalCheckpoint = null;
        await foreach (var page in reader.ReadChangesAsync(table, checkpoint, 5_000, CancellationToken.None))
        {
            records.AddRange(page.Records);
            if (!page.HasMoreRecords)
            {
                terminalCheckpoint = page.DataCheckpoint;
            }
        }

        return (records, Assert.IsType<string>(terminalCheckpoint));
    }

    private static SourceConfiguration SourceConfiguration() => new()
    {
        Name = "credentialed-test",
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
            : throw new InvalidOperationException($"Environment variable '{name}' is required for credentialed tests.");
}
