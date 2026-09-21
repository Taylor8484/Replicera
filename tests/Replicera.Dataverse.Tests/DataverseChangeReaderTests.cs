using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Dataverse.ChangeTracking;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseChangeReaderTests
{
    [Fact]
    public void MapChange_MapsUpsertValues()
    {
        var entity = new Entity("account", Guid.NewGuid())
        {
            ["name"] = "Northwind",
            ["category"] = new OptionSetValue(2)
        };

        var record = DataverseChangeReader.MapChange(new NewOrUpdatedItem { NewOrUpdatedEntity = entity });

        Assert.Equal(ChangeKind.Upsert, record.Kind);
        Assert.Equal("Northwind", record.Values["name"]);
        Assert.Equal(2, record.Values["category"]);
    }

    [Fact]
    public void MapChange_MapsPhysicalDelete()
    {
        var id = Guid.NewGuid();
        var change = new RemovedOrDeletedItem
        {
            RemovedItem = new EntityReference("account", id)
        };

        var record = DataverseChangeReader.MapChange(change);

        Assert.Equal(id, record.Id);
        Assert.Equal(ChangeKind.Delete, record.Kind);
        Assert.Empty(record.Values);
    }

    [Fact]
    public async Task ReadChangesAsync_PreservesOpaqueCheckpointAndAdvancesPagingCookie()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var service = new FakeService(
            Response(
                [new NewOrUpdatedItem { NewOrUpdatedEntity = new Entity("account", firstId) }],
                moreRecords: true,
                pagingCookie: "opaque-cookie",
                dataToken: "intermediate-token"),
            Response(
                [new RemovedOrDeletedItem { RemovedItem = new EntityReference("account", secondId) }],
                moreRecords: false,
                pagingCookie: null,
                dataToken: "terminal-token"));
        var reader = new DataverseChangeReader(service);

        var pages = new List<SourcePage>();
        await foreach (var page in reader.ReadChangesAsync(Table(), "opaque-current-token", 123, CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        Assert.Null(pages[0].DataCheckpoint);
        Assert.Equal("terminal-token", pages[1].DataCheckpoint);
        Assert.Equal(ChangeKind.Upsert, Assert.Single(pages[0].Records).Kind);
        Assert.Equal(ChangeKind.Delete, Assert.Single(pages[1].Records).Kind);
        Assert.Collection(
            service.Requests,
            request =>
            {
                Assert.Equal("opaque-current-token", request.DataVersion);
                Assert.Equal(1, request.PageInfo.PageNumber);
                Assert.Null(request.PageInfo.PagingCookie);
                Assert.Equal(123, request.PageInfo.Count);
            },
            request =>
            {
                Assert.Equal("opaque-current-token", request.DataVersion);
                Assert.Equal(2, request.PageInfo.PageNumber);
                Assert.Equal("opaque-cookie", request.PageInfo.PagingCookie);
                Assert.Equal(123, request.PageInfo.Count);
            });
    }

    [Fact]
    public async Task ReadChangesAsync_ClassifiesInvalidCheckpointWithoutExposingIt()
    {
        const string checkpoint = "sensitive-invalid-checkpoint";
        var reader = new DataverseChangeReader(new ThrowingService(
            new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040203) },
                $"Invalid argument: {checkpoint}")));

        var exception = await Assert.ThrowsAsync<RepliceraException>(async () =>
        {
            await foreach (var _ in reader.ReadChangesAsync(Table(), checkpoint, 100, CancellationToken.None))
            {
            }
        });

        Assert.Equal(ErrorCategory.ExpiredCheckpoint, exception.Category);
        Assert.DoesNotContain(checkpoint, exception.ToString(), StringComparison.Ordinal);
    }

    private static RetrieveEntityChangesResponse Response(
        IReadOnlyList<IChangedItem> changes,
        bool moreRecords,
        string? pagingCookie,
        string dataToken)
    {
        var response = new RetrieveEntityChangesResponse();
        response.Results["EntityChanges"] = new BusinessEntityChanges
        {
            Changes = new BusinessEntityChangesCollection(changes.ToList()),
            MoreRecords = moreRecords,
            PagingCookie = pagingCookie,
            DataToken = dataToken
        };
        return response;
    }

    private static TableDefinition Table() => new(
        "account",
        "accounts",
        "account",
        [new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true }]);

    private sealed class FakeService(params RetrieveEntityChangesResponse[] responses) : IDataverseService
    {
        private readonly Queue<RetrieveEntityChangesResponse> responses = new(responses);

        public List<RetrieveEntityChangesRequest> Requests { get; } = [];

        public Task<OrganizationResponse> ExecuteAsync(
            OrganizationRequest request,
            CancellationToken cancellationToken)
        {
            var changes = Assert.IsType<RetrieveEntityChangesRequest>(request);
            Requests.Add(new RetrieveEntityChangesRequest
            {
                EntityName = changes.EntityName,
                Columns = changes.Columns,
                DataVersion = changes.DataVersion,
                PageInfo = new Microsoft.Xrm.Sdk.Query.PagingInfo
                {
                    Count = changes.PageInfo.Count,
                    PageNumber = changes.PageInfo.PageNumber,
                    PagingCookie = changes.PageInfo.PagingCookie
                }
            });
            return Task.FromResult<OrganizationResponse>(responses.Dequeue());
        }
    }

    private sealed class ThrowingService(Exception exception) : IDataverseService
    {
        public Task<OrganizationResponse> ExecuteAsync(
            OrganizationRequest request,
            CancellationToken cancellationToken) => Task.FromException<OrganizationResponse>(exception);
    }
}
