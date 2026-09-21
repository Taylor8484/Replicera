using System.Reflection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Replicera.Dataverse.Security;

namespace Replicera.Dataverse.Tests;

public sealed class DataversePermissionBootstrapperTests
{
    [Fact]
    public async Task ApplyAsync_CreatesGlobalReaderRoleAndAssignsCurrentUser()
    {
        var userId = Guid.NewGuid();
        var businessUnitId = Guid.NewGuid();
        var privilegeId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var customizerRoleId = Guid.NewGuid();
        var service = new QueueService(
            Response<WhoAmIResponse>(("UserId", userId), ("BusinessUnitId", businessUnitId)),
            Response<RetrieveAllEntitiesResponse>(("EntityMetadata", new[] { TableWithReadPrivilege(privilegeId) })),
            Response<RetrieveMultipleResponse>(("EntityCollection", new EntityCollection())),
            Response<CreateResponse>(("id", roleId)),
            new ReplacePrivilegesRoleResponse(),
            Response<RetrieveMultipleResponse>(("EntityCollection", new EntityCollection())),
            new AssociateResponse(),
            Response<RetrieveMultipleResponse>(("EntityCollection", new EntityCollection([new Entity("role", customizerRoleId)]))),
            Response<RetrieveMultipleResponse>(("EntityCollection", new EntityCollection())),
            new AssociateResponse());

        var result = await new DataversePermissionBootstrapper(service).ApplyAsync(
            DataversePermissionBootstrapper.DefaultRoleName,
            CancellationToken.None);

        Assert.True(result.RoleCreated);
        Assert.True(result.RoleAssigned);
        Assert.True(result.SystemCustomizerAssigned);
        Assert.Equal(1, result.TableReadPrivileges);
        var metadataRequest = Assert.IsType<RetrieveAllEntitiesRequest>(service.Requests[1]);
        Assert.True(metadataRequest.EntityFilters.HasFlag(EntityFilters.Privileges));
        var replace = Assert.IsType<ReplacePrivilegesRoleRequest>(service.Requests[4]);
        var granted = Assert.Single(replace.Privileges);
        Assert.Equal(privilegeId, granted.PrivilegeId);
        Assert.Equal(PrivilegeDepth.Global, granted.Depth);
        var associate = Assert.IsType<AssociateRequest>(service.Requests[6]);
        Assert.Equal(userId, associate.Target.Id);
        Assert.Equal(roleId, Assert.Single(associate.RelatedEntities).Id);
        var customizerAssociate = Assert.IsType<AssociateRequest>(service.Requests[9]);
        Assert.Equal(customizerRoleId, Assert.Single(customizerAssociate.RelatedEntities).Id);
        var customizerQuery = Assert.IsType<RetrieveMultipleRequest>(service.Requests[7]);
        var query = Assert.IsType<Microsoft.Xrm.Sdk.Query.QueryExpression>(customizerQuery.Query);
        Assert.Contains(
            query.Criteria.Conditions,
            condition => condition.AttributeName == "roletemplateid" &&
                         Assert.Single(condition.Values).Equals(DataversePermissionBootstrapper.SystemCustomizerRoleTemplateId));
    }

    private static EntityMetadata TableWithReadPrivilege(Guid privilegeId)
    {
        var privilege = (SecurityPrivilegeMetadata)Activator.CreateInstance(
            typeof(SecurityPrivilegeMetadata), nonPublic: true)!;
        Set(privilege, nameof(SecurityPrivilegeMetadata.PrivilegeId), privilegeId);
        Set(privilege, nameof(SecurityPrivilegeMetadata.PrivilegeType), PrivilegeType.Read);
        Set(privilege, nameof(SecurityPrivilegeMetadata.CanBeGlobal), true);
        var table = new EntityMetadata();
        Set(table, nameof(EntityMetadata.Privileges), new[] { privilege });
        return table;
    }

    private static void Set<T>(object target, string property, T value) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(target, value);

    private static T Response<T>(params (string Name, object Value)[] values)
        where T : OrganizationResponse, new()
    {
        var response = new T();
        foreach (var (name, value) in values)
        {
            response.Results[name] = value;
        }

        return response;
    }

    private sealed class QueueService(params OrganizationResponse[] responses) : IDataverseService
    {
        private readonly Queue<OrganizationResponse> responses = new(responses);
        public List<OrganizationRequest> Requests { get; } = [];

        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses.Dequeue());
        }
    }
}
