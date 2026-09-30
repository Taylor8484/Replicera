using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Replicera.Core.Errors;
using Replicera.Dataverse.ChangeTracking;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseChangeTrackingManagerTests
{
    [Fact]
    public async Task GetStatusAsync_ReportsEnabledTable()
    {
        var service = new MetadataService(Table(enabled: true, customizable: true, canEnable: false));

        var status = await new DataverseChangeTrackingManager(service).GetStatusAsync("account", CancellationToken.None);

        Assert.True(status.IsEnabled);
        Assert.False(status.CanEnable);
        Assert.Null(status.BlockedReason);
    }

    [Fact]
    public async Task GetStatusAsync_AllowsEnablingWhenDataversePermitsIt()
    {
        var service = new MetadataService(Table(enabled: false, customizable: true, canEnable: true));

        var status = await new DataverseChangeTrackingManager(service).GetStatusAsync("account", CancellationToken.None);

        Assert.False(status.IsEnabled);
        Assert.True(status.CanEnable);
        Assert.Null(status.BlockedReason);
    }

    [Fact]
    public async Task GetStatusAsync_RefusesWhenDataverseDisallowsChangeTracking()
    {
        var service = new MetadataService(Table(enabled: false, customizable: true, canEnable: false));

        var status = await new DataverseChangeTrackingManager(service).GetStatusAsync("account", CancellationToken.None);

        Assert.False(status.CanEnable);
        Assert.Equal("Dataverse does not allow change tracking to be enabled for this table.", status.BlockedReason);
    }

    [Fact]
    public async Task GetStatusAsync_RefusesWhenTableCannotBeCustomized()
    {
        var service = new MetadataService(Table(enabled: false, customizable: false, canEnable: true));

        var status = await new DataverseChangeTrackingManager(service).GetStatusAsync("account", CancellationToken.None);

        Assert.False(status.CanEnable);
        Assert.Equal("Dataverse reports that the table cannot be customized.", status.BlockedReason);
    }

    [Fact]
    public async Task EnableAsync_RefusesWithoutUpdatingWhenDataverseDisallowsChangeTracking()
    {
        var service = new MetadataService(Table(enabled: false, customizable: true, canEnable: false));

        var error = await Assert.ThrowsAsync<RepliceraException>(
            () => new DataverseChangeTrackingManager(service).EnableAsync("account", CancellationToken.None));

        Assert.Equal(ErrorCategory.UnsupportedMetadata, error.Category);
        Assert.Contains("'account'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(service.Requests, request => request is UpdateEntityRequest);
    }

    [Fact]
    public async Task EnableAsync_UpdatesChangeTrackingWhenAllowed()
    {
        var service = new MetadataService(Table(enabled: false, customizable: true, canEnable: true));

        await new DataverseChangeTrackingManager(service).EnableAsync("account", CancellationToken.None);

        var update = Assert.Single(service.Requests.OfType<UpdateEntityRequest>());
        Assert.Equal("account", update.Entity.LogicalName);
        Assert.True(update.Entity.ChangeTrackingEnabled);
    }

    private static EntityMetadata Table(bool enabled, bool customizable, bool canEnable) => new()
    {
        LogicalName = "account",
        ChangeTrackingEnabled = enabled,
        IsCustomizable = new BooleanManagedProperty(customizable),
        CanChangeTrackingBeEnabled = new BooleanManagedProperty(canEnable)
    };

    private sealed class MetadataService(EntityMetadata metadata) : IDataverseService
    {
        public List<OrganizationRequest> Requests { get; } = [];

        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OrganizationResponse response = request switch
            {
                RetrieveEntityRequest => new RetrieveEntityResponse { Results = { ["EntityMetadata"] = metadata } },
                UpdateEntityRequest => new UpdateEntityResponse(),
                _ => throw new InvalidOperationException($"Unexpected request '{request.RequestName}'.")
            };
            return Task.FromResult(response);
        }
    }
}
