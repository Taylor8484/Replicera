using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;
using Replicera.Dataverse.Metadata;

namespace Replicera.Dataverse;

public sealed class DataverseSource(IDataverseService service) : ISourceConnection, ISourceMetadataReader
{
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        _ = await service.ExecuteAsync(new WhoAmIRequest(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken cancellationToken)
    {
        var response = (RetrieveAllEntitiesResponse)await service.ExecuteAsync(
            new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            },
            cancellationToken).ConfigureAwait(false);

        return response.EntityMetadata
            .Where(entity => !string.IsNullOrWhiteSpace(entity.LogicalName))
            .Select(entity => entity.LogicalName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<TableDefinition> GetTableAsync(
        string logicalName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var response = (RetrieveEntityResponse)await service.ExecuteAsync(
            new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Entity | EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            },
            cancellationToken).ConfigureAwait(false);

        return DataverseMetadataTranslator.Translate(response.EntityMetadata, logicalName);
    }
}
