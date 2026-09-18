using Elevate.Core.Providers;

namespace Elevate.Audit.Collectors;

/// <summary>
/// Every URL the auditor calls, in one place, so the docs' list of reads can be checked against it.
/// Everything is Microsoft Graph v1.0 except two reads on beta: <see cref="RoleDefinitions"/>, because
/// <c>isPrivileged</c> exists only on the beta <c>unifiedRoleDefinition</c>, and <see cref="GroupMembers"/>,
/// because v1.0 <c>/groups/{id}/members</c> has a documented known issue that omits service principals, and the
/// <c>$expand=members</c> workaround caps at 20 objects.
/// </summary>
public static class GraphUrls
{
    public static Uri Organization => Graph("/organization?$select=id,displayName");

    public static Uri RoleDefinitions => Beta("/roleManagement/directory/roleDefinitions?$select=id,templateId,displayName,isPrivileged,isBuiltIn");

    public static Uri RoleAssignmentInstances => Graph("/roleManagement/directory/roleAssignmentScheduleInstances?$expand=principal");

    public static Uri RoleEligibilityInstances => Graph("/roleManagement/directory/roleEligibilityScheduleInstances?$expand=principal");

    public static Uri RoleAssignableGroups => Graph("/groups?$filter=isAssignableToRole eq true&$select=id,displayName,isAssignableToRole,groupTypes,securityEnabled,mailEnabled,visibility");

    public static Uri Group(string id) => Graph($"/groups/{Escape(id)}?$select=id,displayName,isAssignableToRole,groupTypes,securityEnabled,mailEnabled,visibility");

    public static Uri GroupMembers(string id) => Beta($"/groups/{Escape(id)}/members?$select=id,displayName,userPrincipalName,userType,accountEnabled,servicePrincipalType&$top=999");

    /// <summary>Flattened membership, used only as a verbose-mode cross-check count against the breadth-first walk (which yields the membership path and is used for findings).</summary>
    public static Uri GroupTransitiveMembers(string id) => Graph($"/groups/{Escape(id)}/transitiveMembers?$select=id&$top=999");

    /// <summary>Reads <c>userType</c> and <c>accountEnabled</c> back for users whose projection omitted them; keep the id count small, the filter goes in the URL.</summary>
    public static Uri UsersByIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var literals = string.Join(",", ids.Select(id => $"'{GraphTransport.OdataEscaped(id)}'"));
        return Graph($"/users?$select=id,displayName,userPrincipalName,userType,accountEnabled&$filter=id in ({literals})");
    }

    public static Uri GroupPimAssignments(string id) => Graph($"/identityGovernance/privilegedAccess/group/assignmentScheduleInstances?$filter=groupId eq '{GraphTransport.OdataEscaped(id)}'");

    public static Uri GroupPimEligibilities(string id) => Graph($"/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances?$filter=groupId eq '{GraphTransport.OdataEscaped(id)}'");

    public static Uri GetByIds => Graph("/directoryObjects/getByIds");

    /// <summary>
    /// The PIM entries of the directory audit log from <paramref name="since"/> on: the only record of who
    /// actually activated an eligibility. <c>loggedByService eq 'PIM'</c> covers both directory roles
    /// (category <c>RoleManagement</c>) and PIM for Groups; Azure resource activations are not here, they
    /// come from ARM's own request history. Bounded by the tenant's audit-log retention, 30 days by default.
    /// </summary>
    public static Uri DirectoryAudits(DateTimeOffset since) =>
        Graph($"/auditLogs/directoryAudits?$filter=loggedByService eq 'PIM' and activityDateTime ge {since.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}&$top=999");

    public static Uri ManagementGroups => Arm("/providers/Microsoft.Management/managementGroups", "2021-04-01");

    public static Uri Subscriptions => Arm("/subscriptions", "2022-12-01");

    /// <summary>All assignments at the subscription, its children, and inherited from above.</summary>
    public static Uri SubscriptionRoleAssignments(string subscriptionId) => Arm($"/subscriptions/{Escape(subscriptionId)}/providers/Microsoft.Authorization/roleAssignments", "2022-04-01");

    public static Uri ManagementGroupRoleAssignments(string name) => Arm($"/providers/Microsoft.Management/managementGroups/{Escape(name)}/providers/Microsoft.Authorization/roleAssignments", "2022-04-01", "$filter=atScope()");

    public static Uri SubscriptionAssignmentInstances(string subscriptionId) => Arm($"/subscriptions/{Escape(subscriptionId)}/providers/Microsoft.Authorization/roleAssignmentScheduleInstances", "2020-10-01");

    public static Uri SubscriptionEligibilityInstances(string subscriptionId) => Arm($"/subscriptions/{Escape(subscriptionId)}/providers/Microsoft.Authorization/roleEligibilityScheduleInstances", "2020-10-01");

    /// <summary>Azure PIM request history: the activations of an Azure resource eligibility. Reader is enough; no new scope.</summary>
    public static Uri SubscriptionAssignmentRequests(string subscriptionId) => Arm($"/subscriptions/{Escape(subscriptionId)}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests", "2020-10-01");

    public static Uri SubscriptionRoleDefinitions(string subscriptionId) => Arm($"/subscriptions/{Escape(subscriptionId)}/providers/Microsoft.Authorization/roleDefinitions", "2022-04-01");

    private static Uri Graph(string path) => new(GraphTransport.GraphBase + path);

    private static Uri Beta(string path) => new(GraphTransport.GraphBetaBase + path);

    private static Uri Arm(string path, string apiVersion, string? extra = null) =>
        new($"{GraphTransport.ArmBase}{path}?api-version={apiVersion}{(extra is null ? string.Empty : "&" + extra)}");

    private static string Escape(string id) => Uri.EscapeDataString(id);
}
