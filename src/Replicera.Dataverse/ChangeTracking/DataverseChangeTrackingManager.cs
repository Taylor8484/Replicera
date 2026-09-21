using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Replicera.Core.Abstractions;

namespace Replicera.Dataverse.ChangeTracking;

public sealed class DataverseChangeTrackingManager(IDataverseService service) : IChangeTrackingManager
{
    public async Task<ChangeTrackingStatus> GetStatusAsync(
        string logicalName,
        CancellationToken cancellationToken)
    {
        var metadata = await RetrieveAsync(logicalName, cancellationToken).ConfigureAwait(false);
        var enabled = metadata.ChangeTrackingEnabled == true;
        var canEnable = metadata.IsCustomizable?.Value != false;
        return new ChangeTrackingStatus(
            enabled,
            !enabled && canEnable,
            enabled || canEnable ? null : "Dataverse reports that the table cannot be customized.");
    }

    public async Task EnableAsync(string logicalName, CancellationToken cancellationToken)
    {
        var metadata = await RetrieveAsync(logicalName, cancellationToken).ConfigureAwait(false);
        if (metadata.ChangeTrackingEnabled == true)
        {
            return;
        }

        if (metadata.IsCustomizable?.Value == false)
        {
            throw new InvalidOperationException($"Change tracking cannot be enabled for '{logicalName}'.");
        }

        metadata.ChangeTrackingEnabled = true;
        _ = await service.ExecuteAsync(
            new UpdateEntityRequest { Entity = metadata },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EntityMetadata> RetrieveAsync(
        string logicalName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        var response = (RetrieveEntityResponse)await service.ExecuteAsync(
            new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            },
            cancellationToken).ConfigureAwait(false);
        return response.EntityMetadata;
    }
}
