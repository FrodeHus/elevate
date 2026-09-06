using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>PIM for Azure resource roles through Azure Resource Manager. Port of the Swift <c>AzureResourceProvider</c>.</summary>
public sealed class AzureResourceProvider : IPimProvider
{
    private readonly GraphTransport _transport;

    public AzureResourceProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens, GraphTransport.MapArmError);

    public RoleScopeKind Kind => RoleScopeKind.AzureResource;

    public IReadOnlyList<string> Scopes => Auth.Scopes.ArmAll;

    // MARK: Wire models

    private sealed record Named(string? DisplayName, string? Type, string? Id);

    private sealed record Expanded(Named? Scope, Named? RoleDefinition, Named? Principal);

    private sealed record Properties(
        string Scope,
        string RoleDefinitionId,
        string? PrincipalId,
        string? Status,
        string? AssignmentType,
        string? RoleEligibilityScheduleId,
        string? LinkedRoleEligibilityScheduleId,
        DateTimeOffset? StartDateTime,
        DateTimeOffset? EndDateTime,
        DateTimeOffset? CreatedOn,
        ScheduleInfo? ScheduleInfo,
        Expanded? ExpandedProperties,
        string? MemberType);

    private sealed record Instance(string Name, string Id, Properties Properties);

    /// <summary>One page of an ARM list; ARM names the continuation <c>nextLink</c>, not <c>@odata.nextLink</c>.</summary>
    private sealed record ArmPage<T>(IReadOnlyList<T>? Value, string? NextLink);

    private static readonly IReadOnlySet<string> PendingStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "PendingApproval", "PendingAdminDecision", "PendingApprovalProvisioning",
    };

    /// <summary>An ARM URL for <paramref name="path"/> with <c>api-version</c> first and the rest of the query sorted.</summary>
    internal static Uri ArmUrl(string path, string apiVersion = "2020-10-01", IReadOnlyDictionary<string, string>? query = null)
    {
        var builder = new StringBuilder(GraphTransport.ArmBase)
            .Append('/').Append(path)
            .Append("?api-version=").Append(GraphTransport.PercentEncodeQuery(apiVersion));

        foreach (var (key, value) in (query ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append('&')
                .Append(GraphTransport.PercentEncodeQuery(key))
                .Append('=')
                .Append(GraphTransport.PercentEncodeQuery(value));
        }

        return Uri.TryCreate(builder.ToString(), UriKind.Absolute, out var url)
            ? url
            : throw new PimException(PimErrorKind.Unexpected, $"Bad ARM path {path}", 0);
    }

    /// <summary>A scope's path form for an ARM URL: no leading or trailing slash.</summary>
    private static string Trimmed(string scope) => scope.Trim('/');

    /// <summary>GETs every page of an ARM list, following <c>nextLink</c>.</summary>
    private async Task<IReadOnlyList<T>> ListAllAsync<T>(
        Identity identity, string tenantId, Uri url, CancellationToken ct)
    {
        Uri? next = url;
        var all = new List<T>();
        while (next is { } current)
        {
            var response = await _transport.GetAsync(identity, tenantId, current, Scopes, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<ArmPage<T>>(response.Body, GraphJson.Options);
            if (page?.Value is { } items)
            {
                all.AddRange(items);
            }

            next = page?.NextLink is { } link && Uri.TryCreate(link, UriKind.Absolute, out var parsed) ? parsed : null;
        }

        return all;
    }

    /// <summary>The scope's display name and kind, e.g. "Pay-As-You-Go · subscription".</summary>
    private static string? Caption(Expanded? expanded)
    {
        if (expanded?.Scope is not { DisplayName: { } name })
        {
            return null;
        }

        var type = expanded.Scope.Type?.ToLowerInvariant() switch
        {
            "subscription" => "subscription",
            "resourcegroup" => "resource group",
            "managementgroup" => "management group",
            { } other => other,
            null => null,
        };

        return type is null ? name : string.Create(CultureInfo.InvariantCulture, $"{name} · {type}");
    }

    // MARK: Reads

    public async Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var items = await ListAllAsync<Instance>(identity, tenant.TenantId, EligibilityUrl(), ct).ConfigureAwait(false);

        var seen = new HashSet<RoleScope>();
        var roles = new List<EligibleRole>();
        foreach (var i in items)
        {
            var scope = new AzureResourceScope(i.Properties.Scope, i.Properties.RoleDefinitionId);
            if (!seen.Add(scope))
            {
                continue;
            }

            var viaGroup = string.Equals(i.Properties.MemberType, "Group", StringComparison.OrdinalIgnoreCase)
                ? i.Properties.ExpandedProperties?.Principal?.DisplayName ?? "group"
                : null;

            roles.Add(new EligibleRole(
                new RoleKey(identity.Id, tenant.TenantId, scope),
                i.Properties.ExpandedProperties?.RoleDefinition?.DisplayName ?? i.Properties.RoleDefinitionId,
                RoleSource.Discovered,
                RolePolicy.ManualDefault,
                Detail: Caption(i.Properties.ExpandedProperties),
                ViaGroup: viaGroup));
        }

        return
        [
            .. roles
                .OrderBy(r => r.DisplayName, StringComparer.Ordinal)
                .ThenBy(r => r.Detail ?? "", StringComparer.Ordinal),
        ];
    }

    public async Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var instances = await ListAllAsync<Instance>(
            identity, tenant.TenantId,
            ArmUrl("providers/Microsoft.Authorization/roleAssignmentScheduleInstances", query: AsTarget),
            ct).ConfigureAwait(false);

        var requests = await ListAllAsync<Instance>(
            identity, tenant.TenantId,
            ArmUrl("providers/Microsoft.Authorization/roleAssignmentScheduleRequests", query: AsTarget),
            ct).ConfigureAwait(false);

        var result = new Dictionary<RoleKey, ActiveAssignment>();
        foreach (var i in instances.Where(i => i.Properties.AssignmentType == "Activated"))
        {
            var key = Key(identity, tenant.TenantId, i.Properties);
            result[key] = new ActiveAssignment(
                key, i.Name, i.Properties.StartDateTime ?? DateTimeOffset.UtcNow,
                i.Properties.EndDateTime, AssignmentStatus.Active);
        }

        foreach (var r in requests.Where(r => PendingStatuses.Contains(r.Properties.Status ?? "")))
        {
            var key = Key(identity, tenant.TenantId, r.Properties);
            if (result.ContainsKey(key))
            {
                continue;
            }

            result[key] = new ActiveAssignment(
                key, r.Name,
                r.Properties.ScheduleInfo?.StartDateTime ?? r.Properties.CreatedOn ?? DateTimeOffset.UtcNow,
                null, AssignmentStatus.PendingApproval);
        }

        // The requests list is unfiltered, so a booked-ahead request is already in hand.
        foreach (var u in requests.Where(u => !ScheduleRules.IsSettledOrPending(u.Properties.Status)))
        {
            if (u.Properties.ScheduleInfo?.StartDateTime is not { } start || !ScheduleRules.IsFuture(start))
            {
                continue;
            }

            var key = Key(identity, tenant.TenantId, u.Properties);
            if (result.ContainsKey(key))
            {
                continue;
            }

            var end = ScheduleRules.End(
                u.Properties.ScheduleInfo?.Expiration?.EndDateTime,
                u.Properties.ScheduleInfo?.Expiration?.Duration,
                start);
            result[key] = new ActiveAssignment(key, u.Name, start, end, AssignmentStatus.Scheduled);
        }

        return [.. result.Values];
    }

    private static readonly IReadOnlyDictionary<string, string> AsTarget =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["$filter"] = "asTarget()" };

    private static Uri EligibilityUrl() =>
        ArmUrl("providers/Microsoft.Authorization/roleEligibilityScheduleInstances", query: AsTarget);

    private static RoleKey Key(Identity identity, string tenantId, Properties properties) =>
        new(identity.Id, tenantId, new AzureResourceScope(properties.Scope, properties.RoleDefinitionId));

    // MARK: Policy

    private sealed record PolicyProperties(string? RoleDefinitionId, IReadOnlyList<PolicyRules.Rule>? EffectiveRules);

    private sealed record PolicyAssignment(string Name, PolicyProperties Properties);

    public async Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (role.Key.Scope is not AzureResourceScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var url = ArmUrl(Trimmed(scope.Scope) + "/providers/Microsoft.Authorization/roleManagementPolicyAssignments");
        var assignments = await ListAllAsync<PolicyAssignment>(identity, role.Key.TenantId, url, ct).ConfigureAwait(false);

        var match = assignments.FirstOrDefault(a =>
            string.Equals(a.Properties.RoleDefinitionId, scope.RoleDefinitionId, StringComparison.OrdinalIgnoreCase));
        return match?.Properties.EffectiveRules is { } rules ? PolicyRules.Apply(rules) : RolePolicy.ManualDefault;
    }

    // MARK: Activation

    private sealed record RoleDefinition(string Id, RoleDefinition.Props Properties)
    {
        public sealed record Props(string? RoleName);
    }

    /// <summary>Manual roles carry a role <em>name</em>; ARM wants the definition id at that scope.</summary>
    internal async Task<string> ResolveRoleDefinitionIdAsync(
        string nameOrId, string scope, Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nameOrId);

        if (nameOrId.Contains('/', StringComparison.Ordinal))
        {
            return nameOrId;
        }

        var url = ArmUrl(
            Trimmed(scope) + "/providers/Microsoft.Authorization/roleDefinitions",
            "2022-04-01",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["$filter"] = $"roleName eq '{GraphTransport.OdataEscaped(nameOrId)}'",
            });

        var definitions = await ListAllAsync<RoleDefinition>(identity, tenantId, url, ct).ConfigureAwait(false);
        return definitions.Count > 0 ? definitions[0].Id : throw new PimException(PimErrorKind.NotEligible);
    }

    /// <summary>Finds the caller's eligibility for a scope + role; ARM needs its principal id and schedule name to activate.</summary>
    private async Task<(string PrincipalId, string ScheduleName)> EligibilityAsync(
        string scope, string roleDefinitionId, Identity identity, string tenantId, CancellationToken ct)
    {
        var items = await ListAllAsync<Instance>(identity, tenantId, EligibilityUrl(), ct).ConfigureAwait(false);
        var match = items.FirstOrDefault(i =>
            string.Equals(i.Properties.Scope, scope, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.Properties.RoleDefinitionId, roleDefinitionId, StringComparison.OrdinalIgnoreCase));

        if (match?.Properties.PrincipalId is not { } principal
            || match.Properties.RoleEligibilityScheduleId is not { } schedule)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        // Swift: components(separatedBy: "/").last ?? schedule — a trailing slash yields "".
        return (principal, schedule[(schedule.LastIndexOf('/') + 1)..]);
    }

    /// <summary>
    /// The principal to put in a Self* request: always the caller. An eligibility inherited
    /// through a group carries the <em>group's</em> principal id, and ARM refuses a request naming it
    /// ("The requestor … does not have permissions for this request"). The caller's object id
    /// in this tenant comes from the ARM token; an opaque token falls back to the eligibility's id.
    /// </summary>
    private async Task<string> RequestPrincipalIdAsync(
        string eligibilityPrincipalId, Identity identity, string tenantId, CancellationToken ct)
    {
        try
        {
            var token = await _transport.Tokens.AccessTokenAsync(identity, tenantId, Scopes, ct).ConfigureAwait(false);
            if (AccessTokenClaims.ObjectId(token) is { } oid)
            {
                return oid;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PimException)
        {
            // Fall through to the eligibility's own principal, like the Swift `try?`.
        }

        return eligibilityPrincipalId;
    }

    private static Uri RequestUrl(string scope) =>
        ArmUrl(Trimmed(scope) + "/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/"
            + Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());

    private static byte[] Encode(JsonObject properties) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["properties"] = properties }.ToJsonString());

    public async Task<ActiveAssignment> ActivateAsync(
        ActivationRequest request, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RoleKey.Scope is not AzureResourceScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var tenantId = request.RoleKey.TenantId;
        var roleDefinitionId = await ResolveRoleDefinitionIdAsync(scope.RoleDefinitionId, scope.Scope, identity, tenantId, ct)
            .ConfigureAwait(false);
        var eligibility = await EligibilityAsync(scope.Scope, roleDefinitionId, identity, tenantId, ct).ConfigureAwait(false);
        var principalId = await RequestPrincipalIdAsync(eligibility.PrincipalId, identity, tenantId, ct).ConfigureAwait(false);

        var props = new JsonObject
        {
            ["principalId"] = principalId,
            ["roleDefinitionId"] = roleDefinitionId,
            ["requestType"] = "SelfActivate",
            ["linkedRoleEligibilityScheduleId"] = eligibility.ScheduleName,
            ["justification"] = request.Justification,
            ["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = GraphJson.EncoderDateString(request.StartDateTime ?? DateTimeOffset.UtcNow),
                ["expiration"] = new JsonObject
                {
                    ["type"] = "AfterDuration",
                    ["duration"] = Iso8601Duration.Format(request.Duration),
                },
            },
        };

        if (request.Ticket is { } ticket)
        {
            props["ticketInfo"] = new JsonObject
            {
                ["ticketNumber"] = ticket.Number,
                ["ticketSystem"] = ticket.System,
            };
        }

        var response = await _transport
            .PutAsync(identity, tenantId, RequestUrl(scope.Scope), Scopes, Encode(props), ct).ConfigureAwait(false);
        var created = JsonSerializer.Deserialize<Instance>(response.Body, GraphJson.Options)
            ?? throw new PimException(PimErrorKind.Unexpected, "Empty response body");

        var start = ScheduleRules.Effective(created.Properties.ScheduleInfo?.StartDateTime, request.StartDateTime);
        var end = ScheduleRules.End(
            created.Properties.ScheduleInfo?.Expiration?.EndDateTime,
            created.Properties.ScheduleInfo?.Expiration?.Duration,
            start, request.Duration);

        var raw = created.Properties.Status ?? "Provisioned";
        var reported = raw switch
        {
            "PendingApproval" or "PendingAdminDecision" or "PendingApprovalProvisioning" => AssignmentStatus.PendingApproval,
            "PendingProvisioning" or "PendingScheduleCreation" or "ScheduleCreated" or "Accepted"
                or "PendingEvaluation" or "ProvisioningStarted" or "PendingExternalProvisioning"
                => AssignmentStatus.PendingProvisioning,
            "Denied" or "Failed" or "Canceled" or "Revoked" or "TimedOut" or "Invalid" or "AdminDenied"
                or "FailedAsResourceIsLocked" => AssignmentStatus.Failed(raw),
            _ => AssignmentStatus.Active,
        };

        // A future start only masks an outcome that would otherwise read as active; pending/failed still win.
        var status = reported == AssignmentStatus.Active && ScheduleRules.IsFuture(start)
            ? AssignmentStatus.Scheduled
            : reported;

        // A manual role is keyed by role name; key the assignment by the id ARM resolved it to.
        var resolvedKey = new RoleKey(
            request.RoleKey.IdentityId, tenantId, new AzureResourceScope(scope.Scope, roleDefinitionId));

        return new ActiveAssignment(
            resolvedKey, created.Name, start,
            status == AssignmentStatus.Active || status == AssignmentStatus.Scheduled ? end : null,
            status);
    }

    public async Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.RoleKey.Scope is not AzureResourceScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var tenantId = assignment.RoleKey.TenantId;
        var roleDefinitionId = await ResolveRoleDefinitionIdAsync(scope.RoleDefinitionId, scope.Scope, identity, tenantId, ct)
            .ConfigureAwait(false);
        var eligibility = await EligibilityAsync(scope.Scope, roleDefinitionId, identity, tenantId, ct).ConfigureAwait(false);
        var principalId = await RequestPrincipalIdAsync(eligibility.PrincipalId, identity, tenantId, ct).ConfigureAwait(false);

        var props = new JsonObject
        {
            ["principalId"] = principalId,
            ["roleDefinitionId"] = roleDefinitionId,
            ["requestType"] = "SelfDeactivate",
            ["linkedRoleEligibilityScheduleId"] = eligibility.ScheduleName,
        };

        await _transport.PutAsync(identity, tenantId, RequestUrl(scope.Scope), Scopes, Encode(props), ct)
            .ConfigureAwait(false);
    }

    public async Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.RoleKey.Scope is not AzureResourceScope scope || assignment.AssignmentId is not { } name)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var url = ArmUrl(
            Trimmed(scope.Scope) + $"/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{name}/cancel");
        await _transport.PostAsync(identity, assignment.RoleKey.TenantId, url, Scopes, [], ct).ConfigureAwait(false);
    }
}
