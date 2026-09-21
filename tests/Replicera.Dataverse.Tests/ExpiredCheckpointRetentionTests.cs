using System.Text.Json;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;
using Replicera.Dataverse.Authentication;
using Replicera.Dataverse.ChangeTracking;

namespace Replicera.Dataverse.Tests;

public sealed class ExpiredCheckpointRetentionTests
{
    [Fact]
    [Trait("Category", "Credentialed")]
    [Trait("Category", "CheckpointExpiry")]
    public async Task CapturedCheckpoint_AfterRetention_IsClassifiedForFullResynchronization()
    {
        var path = Environment.GetEnvironmentVariable("REPLICERA_EXPIRED_CHECKPOINT_STATE");
        Assert.False(string.IsNullOrWhiteSpace(path));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        var tableName = root.GetProperty("table").GetString();
        var checkpoint = root.GetProperty("checkpoint").GetString();
        var verifyAfter = root.GetProperty("verifyAfterUtc").GetDateTimeOffset();
        Assert.True(DateTimeOffset.UtcNow >= verifyAfter, $"Checkpoint is not expected to expire until {verifyAfter:O}.");

        await using var service = DataverseClientFactory.Create(
            SourceConfiguration(),
            new EnvironmentSecretResolver());
        var source = new DataverseSource(service);
        var table = await source.GetTableAsync(tableName!, CancellationToken.None);
        var reader = new DataverseChangeReader(service);
        var exception = await Assert.ThrowsAsync<RepliceraException>(async () =>
        {
            await foreach (var _ in reader.ReadChangesAsync(table, checkpoint, 5_000, CancellationToken.None))
            {
            }
        });

        Assert.Equal(ErrorCategory.ExpiredCheckpoint, exception.Category);
    }

    private static SourceConfiguration SourceConfiguration() => new()
    {
        Name = "retained-checkpoint",
        Url = new Uri(Environment.GetEnvironmentVariable("REPLICERA_DATAVERSE_URL")!),
        TenantId = Guid.Parse(Environment.GetEnvironmentVariable("REPLICERA_DATAVERSE_TENANT_ID")!),
        ClientId = Guid.Parse(Environment.GetEnvironmentVariable("REPLICERA_DATAVERSE_CLIENT_ID")!),
        Authentication = new AuthenticationConfiguration
        {
            Method = AuthenticationMethod.ClientSecret,
            SecretEnvironmentVariable = "REPLICERA_DATAVERSE_CLIENT_SECRET"
        }
    };
}
