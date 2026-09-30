using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Replicera.Core.Abstractions;
using Replicera.Core.Errors;

namespace Replicera.Dataverse.ChangeTracking;

public sealed class DataverseChangeTrackingManager(IDataverseService service) : IChangeTrackingManager
{
    public async Task<ChangeTrackingStatus> GetStatusAsync(
        string logicalName,
        CancellationToken cancellationToken)
    {
        var metadata = await RetrieveAsync(logicalName, cancellationToken).ConfigureAwait(false);
        var enabled = metadata.ChangeTrackingEnabled == true;
        var blockedReason = enabled ? null : BlockedReason(metadata);
        return new ChangeTrackingStatus(enabled, !enabled && blockedReason is null, blockedReason);
    }

    public async Task EnableAsync(string logicalName, CancellationToken cancellationToken)
    {
        var metadata = await RetrieveAsync(logicalName, cancellationToken).ConfigureAwait(false);
        if (metadata.ChangeTrackingEnabled == true)
        {
            return;
        }

        var blockedReason = BlockedReason(metadata);
        if (blockedReason is not null)
        {
            throw new RepliceraException(
                ErrorCategory.UnsupportedMetadata,
                $"Change tracking cannot be enabled for '{logicalName}': {blockedReason}");
        }

        metadata.ChangeTrackingEnabled = true;
        _ = await service.ExecuteAsync(
            new UpdateEntityRequest { Entity = metadata },
            cancellationToken).ConfigureAwait(false);
    }

    private static string? BlockedReason(EntityMetadata metadata) =>
        metadata.IsCustomizable?.Value == false
            ? "Dataverse reports that the table cannot be customized."
            : metadata.CanChangeTrackingBeEnabled?.Value == false
                ? "Dataverse does not allow change tracking to be enabled for this table."
                : null;

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
