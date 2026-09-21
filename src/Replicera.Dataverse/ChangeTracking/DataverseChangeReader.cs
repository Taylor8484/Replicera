using System.Runtime.CompilerServices;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Replicera.Core.Abstractions;
using Replicera.Core.Models;
using Replicera.Dataverse.Errors;

namespace Replicera.Dataverse.ChangeTracking;

public sealed class DataverseChangeReader(IDataverseService service) : ISourceChangeReader
{
    public async IAsyncEnumerable<SourcePage> ReadChangesAsync(
        TableDefinition table,
        string? dataCheckpoint,
        int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (pageSize is < 1 or > 5_000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 5000.");
        }

        var pageNumber = 1;
        string? pagingCookie = null;
        bool moreRecords;

        do
        {
            var request = new RetrieveEntityChangesRequest
            {
                EntityName = table.LogicalName,
                Columns = new ColumnSet(table.Columns.Where(column => column.IsSupported).Select(column => column.LogicalName).ToArray()),
                DataVersion = dataCheckpoint,
                PageInfo = new PagingInfo
                {
                    Count = pageSize,
                    PageNumber = pageNumber,
                    PagingCookie = pagingCookie,
                    ReturnTotalRecordCount = false
                }
            };

            RetrieveEntityChangesResponse response;
            try
            {
                response = (RetrieveEntityChangesResponse)await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault> exception)
            {
                throw DataverseFaultClassifier.Classify(request, exception.Detail);
            }

            var changes = response.EntityChanges;
            var records = changes.Changes.Select(MapChange).ToArray();
            moreRecords = changes.MoreRecords;
            pagingCookie = moreRecords ? changes.PagingCookie : null;

            yield return new SourcePage(
                records,
                pagingCookie,
                moreRecords ? null : changes.DataToken,
                moreRecords);

            pageNumber++;
        }
        while (moreRecords);
    }

    public static SourceRecord MapChange(object change)
    {
        return change switch
        {
            NewOrUpdatedItem item => new SourceRecord(
                item.NewOrUpdatedEntity.Id,
                ChangeKind.Upsert,
                DataverseValueConverter.ConvertAttributes(item.NewOrUpdatedEntity)),
            RemovedOrDeletedItem item => new SourceRecord(
                item.RemovedItem.Id,
                ChangeKind.Delete,
                new Dictionary<string, object?>()),
            _ => throw new NotSupportedException($"Dataverse change type '{change.GetType().FullName}' is not supported.")
        };
    }
}
