using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Replicera.Dataverse.Security;

public sealed class DataversePermissionBootstrapper(IDataverseService service)
{
    public const string DefaultRoleName = "Replicera Pump Reader";
    internal static readonly Guid SystemAdministratorRoleTemplateId = new("627090FF-40A3-4053-8790-584EDC5BE201");
    internal static readonly Guid SystemCustomizerRoleTemplateId = new("119F245C-3CC8-4B62-B31C-D1A046CED15D");

    public async Task<PermissionBootstrapResult> ApplyAsync(
        string roleName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        var who = (WhoAmIResponse)await service.ExecuteAsync(
            new WhoAmIRequest(), cancellationToken).ConfigureAwait(false);
        var metadata = (RetrieveAllEntitiesResponse)await service.ExecuteAsync(
            new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity | EntityFilters.Privileges,
                RetrieveAsIfPublished = false
            }, cancellationToken).ConfigureAwait(false);
        var reads = metadata.EntityMetadata
            .SelectMany(table => table.Privileges ?? [])
            .Where(privilege => privilege.PrivilegeType == PrivilegeType.Read && privilege.CanBeGlobal)
            .DistinctBy(privilege => privilege.PrivilegeId)
            .OrderBy(privilege => privilege.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (reads.Length == 0)
        {
            throw new InvalidOperationException("Dataverse returned no organization-level table Read privileges; the reader role was not changed.");
        }

        var role = await FindRoleAsync(roleName, who.BusinessUnitId, cancellationToken).ConfigureAwait(false);
        var created = role is null;
        var roleId = role?.Id ?? await CreateRoleAsync(roleName, who.BusinessUnitId, cancellationToken).ConfigureAwait(false);
        var current = created
            ? []
            : ((RetrieveRolePrivilegesRoleResponse)await service.ExecuteAsync(
                new RetrieveRolePrivilegesRoleRequest { RoleId = roleId }, cancellationToken).ConfigureAwait(false)).RolePrivileges;
        var desiredIds = reads.Select(item => item.PrivilegeId).ToHashSet();
        var merged = current
            .Where(item => !desiredIds.Contains(item.PrivilegeId))
            .Concat(reads.Select(item => new RolePrivilege
            {
                Depth = PrivilegeDepth.Global,
                PrivilegeId = item.PrivilegeId
            }))
            .ToArray();
        _ = await service.ExecuteAsync(
            new ReplacePrivilegesRoleRequest { RoleId = roleId, Privileges = merged },
            cancellationToken).ConfigureAwait(false);

        var assigned = await IsAssignedAsync(who.UserId, roleId, cancellationToken).ConfigureAwait(false);
        if (!assigned)
        {
            await AssignAsync(who.UserId, roleId, cancellationToken).ConfigureAwait(false);
        }

        var customizer = await FindRoleByTemplateAsync(SystemCustomizerRoleTemplateId, who.BusinessUnitId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The System Customizer role was not found in the application user's business unit.");
        var customizerAssigned = await IsAssignedAsync(who.UserId, customizer.Id, cancellationToken).ConfigureAwait(false);
        if (!customizerAssigned)
        {
            await AssignAsync(who.UserId, customizer.Id, cancellationToken).ConfigureAwait(false);
        }

        return new PermissionBootstrapResult(roleName, roleId, reads.Length, created, !assigned, !customizerAssigned);
    }

    private async Task<Entity?> FindRoleAsync(string roleName, Guid businessUnitId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("role") { ColumnSet = new ColumnSet("roleid", "name") };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, roleName);
        query.Criteria.AddCondition("businessunitid", ConditionOperator.Equal, businessUnitId);
        var response = (RetrieveMultipleResponse)await service.ExecuteAsync(
            new RetrieveMultipleRequest { Query = query }, cancellationToken).ConfigureAwait(false);
        return response.EntityCollection.Entities.SingleOrDefault();
    }

    private async Task<Entity?> FindRoleByTemplateAsync(Guid roleTemplateId, Guid businessUnitId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("role") { ColumnSet = new ColumnSet("roleid") };
        query.Criteria.AddCondition("roletemplateid", ConditionOperator.Equal, roleTemplateId);
        query.Criteria.AddCondition("businessunitid", ConditionOperator.Equal, businessUnitId);
        var response = (RetrieveMultipleResponse)await service.ExecuteAsync(
            new RetrieveMultipleRequest { Query = query }, cancellationToken).ConfigureAwait(false);
        return response.EntityCollection.Entities.SingleOrDefault();
    }

    private async Task<Guid> CreateRoleAsync(string roleName, Guid businessUnitId, CancellationToken cancellationToken)
    {
        var role = new Entity("role")
        {
            ["name"] = roleName,
            ["businessunitid"] = new EntityReference("businessunit", businessUnitId)
        };
        var response = (CreateResponse)await service.ExecuteAsync(
            new CreateRequest { Target = role }, cancellationToken).ConfigureAwait(false);
        return response.id;
    }

    private async Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("role") { ColumnSet = new ColumnSet(false), TopCount = 1 };
        query.Criteria.AddCondition("roleid", ConditionOperator.Equal, roleId);
        var link = query.AddLink("systemuserroles", "roleid", "roleid");
        link.LinkCriteria.AddCondition("systemuserid", ConditionOperator.Equal, userId);
        var response = (RetrieveMultipleResponse)await service.ExecuteAsync(
            new RetrieveMultipleRequest { Query = query }, cancellationToken).ConfigureAwait(false);
        return response.EntityCollection.Entities.Count != 0;
    }

    private async Task AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken) =>
        _ = await service.ExecuteAsync(
            new AssociateRequest
            {
                Target = new EntityReference("systemuser", userId),
                Relationship = new Relationship("systemuserroles_association"),
                RelatedEntities = [new EntityReference("role", roleId)]
            }, cancellationToken).ConfigureAwait(false);
}

public sealed record PermissionBootstrapResult(
    string RoleName,
    Guid RoleId,
    int TableReadPrivileges,
    bool RoleCreated,
    bool RoleAssigned,
    bool SystemCustomizerAssigned);
