using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Replicera.Core.Errors;

namespace Replicera.Dataverse.Security;

public sealed class DataversePermissionBootstrapper(IDataverseService service)
{
    public const string DefaultRoleName = "Replicera Pump Reader";
    internal static readonly Guid SystemAdministratorRoleTemplateId = new("627090FF-40A3-4053-8790-584EDC5BE201");
    internal static readonly Guid SystemCustomizerRoleTemplateId = new("119F245C-3CC8-4B62-B31C-D1A046CED15D");

    public async Task<PermissionBootstrapResult> ApplyAsync(
        string roleName,
        PermissionScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        ArgumentNullException.ThrowIfNull(scope);
        var who = (WhoAmIResponse)await service.ExecuteAsync(
            new WhoAmIRequest(), cancellationToken).ConfigureAwait(false);
        var reads = scope.IsAllTables
            ? await GetAllTableReadPrivilegesAsync(cancellationToken).ConfigureAwait(false)
            : await GetTableReadPrivilegesAsync(scope.Tables!, cancellationToken).ConfigureAwait(false);

        // Custom roles can only be created in the root business unit. Dataverse copies them into every
        // child business unit, and a user can only be assigned the copy in their own business unit.
        var rootBusinessUnitId = await GetRootBusinessUnitIdAsync(cancellationToken).ConfigureAwait(false);
        var role = await FindRoleAsync(roleName, rootBusinessUnitId, cancellationToken).ConfigureAwait(false);
        var created = role is null;
        var roleId = role?.Id ?? await CreateRoleAsync(roleName, rootBusinessUnitId, cancellationToken).ConfigureAwait(false);
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

        var assignableRoleId = who.BusinessUnitId == rootBusinessUnitId
            ? roleId
            : await FindInheritedRoleIdAsync(roleId, roleName, who.BusinessUnitId, cancellationToken).ConfigureAwait(false);
        var assigned = await IsAssignedAsync(who.UserId, assignableRoleId, cancellationToken).ConfigureAwait(false);
        if (!assigned)
        {
            await AssignAsync(who.UserId, assignableRoleId, cancellationToken).ConfigureAwait(false);
        }

        var customizer = await FindRoleByTemplateAsync(SystemCustomizerRoleTemplateId, who.BusinessUnitId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The System Customizer role was not found in the application user's business unit.");
        var customizerAssigned = await IsAssignedAsync(who.UserId, customizer.Id, cancellationToken).ConfigureAwait(false);
        if (!customizerAssigned)
        {
            await AssignAsync(who.UserId, customizer.Id, cancellationToken).ConfigureAwait(false);
        }

        return new PermissionBootstrapResult(
            roleName,
            roleId,
            reads.Length,
            created,
            !assigned,
            !customizerAssigned,
            scope.IsAllTables,
            scope.Tables ?? []);
    }

    private async Task<SecurityPrivilegeMetadata[]> GetAllTableReadPrivilegesAsync(CancellationToken cancellationToken)
    {
        var metadata = (RetrieveAllEntitiesResponse)await service.ExecuteAsync(
            new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity | EntityFilters.Privileges,
                RetrieveAsIfPublished = false
            }, cancellationToken).ConfigureAwait(false);
        var reads = metadata.EntityMetadata
            .SelectMany(table => table.Privileges ?? [])
            .Where(IsGlobalRead)
            .DistinctBy(privilege => privilege.PrivilegeId)
            .OrderBy(privilege => privilege.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return reads.Length == 0
            ? throw new InvalidOperationException("Dataverse returned no organization-level table Read privileges; the reader role was not changed.")
            : reads;
    }

    private async Task<SecurityPrivilegeMetadata[]> GetTableReadPrivilegesAsync(
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        var reads = new List<SecurityPrivilegeMetadata>();
        var missing = new List<string>();
        var withoutRead = new List<string>();
        foreach (var table in tables)
        {
            EntityMetadata metadata;
            try
            {
                metadata = ((RetrieveEntityResponse)await service.ExecuteAsync(
                    new RetrieveEntityRequest
                    {
                        LogicalName = table,
                        EntityFilters = EntityFilters.Entity | EntityFilters.Privileges,
                        RetrieveAsIfPublished = false
                    }, cancellationToken).ConfigureAwait(false)).EntityMetadata;
            }
            catch (RepliceraException exception) when (exception.Category == ErrorCategory.SourceMetadataNotFound)
            {
                missing.Add(table);
                continue;
            }

            var read = (metadata.Privileges ?? []).FirstOrDefault(IsGlobalRead);
            if (read is null)
            {
                withoutRead.Add(table);
                continue;
            }

            reads.Add(read);
        }

        if (missing.Count > 0)
        {
            throw new RepliceraException(
                ErrorCategory.SourceMetadataNotFound,
                $"Dataverse tables were not found: {string.Join(", ", missing)}. The reader role was not changed.");
        }

        if (withoutRead.Count > 0)
        {
            throw new RepliceraException(
                ErrorCategory.UnsupportedMetadata,
                $"Dataverse tables do not support organization-level Read: {string.Join(", ", withoutRead)}. The reader role was not changed.");
        }

        return [.. reads.DistinctBy(privilege => privilege.PrivilegeId)];
    }

    private static bool IsGlobalRead(SecurityPrivilegeMetadata privilege) =>
        privilege.PrivilegeType == PrivilegeType.Read && privilege.CanBeGlobal;

    private async Task<Guid> GetRootBusinessUnitIdAsync(CancellationToken cancellationToken)
    {
        var query = new QueryExpression("businessunit") { ColumnSet = new ColumnSet("businessunitid") };
        query.Criteria.AddCondition("parentbusinessunitid", ConditionOperator.Null);
        var response = (RetrieveMultipleResponse)await service.ExecuteAsync(
            new RetrieveMultipleRequest { Query = query }, cancellationToken).ConfigureAwait(false);
        return response.EntityCollection.Entities.Count == 1
            ? response.EntityCollection.Entities[0].Id
            : throw new RepliceraException(
                ErrorCategory.UnsupportedMetadata,
                "The root business unit could not be determined; the reader role was not changed.");
    }

    private async Task<Guid> FindInheritedRoleIdAsync(
        Guid rootRoleId,
        string roleName,
        Guid businessUnitId,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("role") { ColumnSet = new ColumnSet("roleid") };
        query.Criteria.AddCondition("parentrootroleid", ConditionOperator.Equal, rootRoleId);
        query.Criteria.AddCondition("businessunitid", ConditionOperator.Equal, businessUnitId);
        var response = (RetrieveMultipleResponse)await service.ExecuteAsync(
            new RetrieveMultipleRequest { Query = query }, cancellationToken).ConfigureAwait(false);
        return response.EntityCollection.Entities.SingleOrDefault()?.Id
            ?? throw new RepliceraException(
                ErrorCategory.UnsupportedMetadata,
                $"The role '{roleName}' was updated in the root business unit, but its copy in the application user's business unit was not found; rerun the command once Dataverse has copied the role.");
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
    bool SystemCustomizerAssigned,
    bool AllTables,
    IReadOnlyList<string> Tables);

/// <summary>
/// Selects which tables receive organization-level Read in the reader role. Granting every
/// table exposes all environment data to the pump identity and must be chosen explicitly.
/// </summary>
public sealed record PermissionScope
{
    private PermissionScope(IReadOnlyList<string>? tables) => Tables = tables;

    public static PermissionScope AllTables { get; } = new((IReadOnlyList<string>?)null);

    public IReadOnlyList<string>? Tables { get; }

    public bool IsAllTables => Tables is null;

    public static PermissionScope ForTables(IEnumerable<string> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var names = tables
            .Select(table => table?.Trim() ?? string.Empty)
            .ToArray();
        if (names.Length == 0 || names.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("At least one table is required and table names must not be blank.", nameof(tables));
        }

        return new PermissionScope(names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
