using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>
/// PIM for Groups through Microsoft Graph: eligibility for, and activation of, group membership or ownership.
/// Port of the Swift <c>GroupProvider</c>.
/// </summary>
public sealed class GroupProvider : IPimProvider
{
    internal const string Base = "/identityGovernance/privilegedAccess/group";

    private readonly GraphTransport _transport;

    public GroupProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public RoleScopeKind Kind => RoleScopeKind.Group;

    public IReadOnlyList<string> Scopes => Auth.Scopes.GroupAll;

    // MARK: Wire models

    private sealed record GroupRef(string? Id, string? DisplayName);

    private sealed record Instance(
        string Id,
        string? PrincipalId,
        string GroupId,
        string AccessId,
        string? MemberType,
        string? AssignmentType,
        DateTimeOffset? StartDateTime,
        DateTimeOffset? EndDateTime,
        GroupRef? Group);

    private sealed record ScheduleRequest(
        string Id,
        string Status,
        string GroupId,
        string AccessId,
        DateTimeOffset? CreatedDateTime,
        ScheduleInfo? ScheduleInfo);

    internal static GroupAccess Access(string raw) =>
        string.Equals(raw, "owner", StringComparison.OrdinalIgnoreCase) ? GroupAccess.Owner : GroupAccess.Member;

    private static bool IsGroupMember(string? memberType) =>
        string.Equals(memberType, "group", StringComparison.OrdinalIgnoreCase);

    private const string EligibilityPath =
        Base + "/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)";

    // MARK: Reads

    public async Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var items = await _transport.ListAllAsync<Instance>(
            identity, tenant.TenantId, _transport.GraphUrl(EligibilityPath), Scopes, ct).ConfigureAwait(false);

        var seen = new HashSet<RoleScope>();
        var roles = new List<EligibleRole>();
        foreach (var i in items)
        {
            var access = Access(i.AccessId);
            var scope = new GroupScope(i.GroupId, access);
            if (!seen.Add(scope))
            {
                continue;
            }

            roles.Add(new EligibleRole(
                new RoleKey(identity.Id, tenant.TenantId, scope),
                i.Group?.DisplayName ?? i.GroupId,
                RoleSource.Discovered,
                RolePolicy.ManualDefault,
                Detail: access == GroupAccess.Owner ? "owner" : "member",
                ViaGroup: IsGroupMember(i.MemberType) ? "group" : null));
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

        var instances = await _transport.ListAllAsync<Instance>(
            identity, tenant.TenantId,
            _transport.GraphUrl(Base + "/assignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)"),
            Scopes, ct).ConfigureAwait(false);

        // Widened past PendingApproval so a booked-ahead request, which the service has already
        // turned into a schedule, is the source for the scheduled rows below.
        var requests = await _transport.ListAllAsync<ScheduleRequest>(
            identity, tenant.TenantId,
            _transport.GraphUrl(Base + "/assignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval' or status eq 'ScheduleCreated' or status eq 'Provisioned'"),
            Scopes, ct).ConfigureAwait(false);

        var result = new Dictionary<RoleKey, ActiveAssignment>();
        foreach (var i in instances.Where(i => string.Equals(i.AssignmentType, "activated", StringComparison.OrdinalIgnoreCase)))
        {
            var key = new RoleKey(identity.Id, tenant.TenantId, new GroupScope(i.GroupId, Access(i.AccessId)));
            result[key] = new ActiveAssignment(
                key, i.Id, i.StartDateTime ?? DateTimeOffset.UtcNow, i.EndDateTime, AssignmentStatus.Active);
        }

        foreach (var r in requests.Where(r => r.Status == "PendingApproval"))
        {
            var key = new RoleKey(identity.Id, tenant.TenantId, new GroupScope(r.GroupId, Access(r.AccessId)));
            if (result.ContainsKey(key))
            {
                continue;
            }

            result[key] = new ActiveAssignment(
                key, r.Id,
                r.ScheduleInfo?.StartDateTime ?? r.CreatedDateTime ?? DateTimeOffset.UtcNow,
                null, AssignmentStatus.PendingApproval);
        }

        foreach (var u in requests.Where(r => !ScheduleRules.IsSettledOrPending(r.Status)))
        {
            if (u.ScheduleInfo?.StartDateTime is not { } start || !ScheduleRules.IsFuture(start))
            {
                continue;
            }

            var key = new RoleKey(identity.Id, tenant.TenantId, new GroupScope(u.GroupId, Access(u.AccessId)));
            if (result.ContainsKey(key))
            {
                continue;
            }

            var end = ScheduleRules.End(u.ScheduleInfo?.Expiration?.EndDateTime, u.ScheduleInfo?.Expiration?.Duration, start);
            result[key] = new ActiveAssignment(key, u.Id, start, end, AssignmentStatus.Scheduled);
        }

        return [.. result.Values];
    }

    // MARK: Policy

    private sealed record Policy(string Id, IReadOnlyList<PolicyRules.Rule>? Rules);

    private sealed record PolicyAssignment(string Id, string? RoleDefinitionId, Policy? Policy);

    public async Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (role.Key.Scope is not GroupScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var access = scope.AccessId == GroupAccess.Owner ? "owner" : "member";
        var filter = $"scopeId eq '{GraphTransport.OdataEscaped(scope.GroupId)}' and scopeType eq 'Group' and roleDefinitionId eq '{access}'";
        var encoded = GraphTransport.PercentEncodeQuery(filter);
        var response = await _transport.GetAsync(
            identity, role.Key.TenantId,
            _transport.GraphUrl($"/policies/roleManagementPolicyAssignments?$filter={encoded}&$expand=policy($expand=rules)"),
            Scopes, ct).ConfigureAwait(false);

        var page = JsonSerializer.Deserialize<GraphTransport.Page<PolicyAssignment>>(response.Body, GraphJson.Options);
        var assignments = page?.Value ?? [];
        var rules = assignments.Count > 0 ? assignments[0].Policy?.Rules : null;
        return rules is null ? RolePolicy.ManualDefault : PolicyRules.Apply(rules);
    }

    // MARK: Activate / deactivate

    /// <summary>The eligibility's own principal id (a group when inherited), used only when the token hides the caller's oid.</summary>
    private async Task<string?> EligibilityPrincipalIdAsync(
        string groupId, GroupAccess access, Identity identity, string tenantId, CancellationToken ct)
    {
        var items = await _transport.ListAllAsync<Instance>(
            identity, tenantId, _transport.GraphUrl(EligibilityPath), Scopes, ct).ConfigureAwait(false);
        return items.FirstOrDefault(i => i.GroupId == groupId && Access(i.AccessId) == access)?.PrincipalId;
    }

    /// <summary>Always the caller: an eligibility inherited through another group names that group, which Graph refuses.</summary>
    private async Task<string> RequestPrincipalIdAsync(
        string groupId, GroupAccess access, Identity identity, string tenantId, CancellationToken ct)
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

        return await EligibilityPrincipalIdAsync(groupId, access, identity, tenantId, ct).ConfigureAwait(false)
            ?? throw new PimException(PimErrorKind.NotEligible);
    }

    private static AssignmentStatus Status(string raw) => raw switch
    {
        "PendingApproval" or "PendingAdminDecision" => AssignmentStatus.PendingApproval,
        "PendingProvisioning" or "PendingScheduleCreation" or "ScheduleCreated" => AssignmentStatus.PendingProvisioning,
        "Denied" or "Failed" or "Canceled" or "Revoked" => AssignmentStatus.Failed(raw),
        _ => AssignmentStatus.Active,
    };

    public async Task<ActiveAssignment> ActivateAsync(
        ActivationRequest request, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RoleKey.Scope is not GroupScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var tenantId = request.RoleKey.TenantId;
        var principal = await RequestPrincipalIdAsync(scope.GroupId, scope.AccessId, identity, tenantId, ct)
            .ConfigureAwait(false);

        var body = new JsonObject
        {
            ["action"] = "selfActivate",
            ["principalId"] = principal,
            ["groupId"] = scope.GroupId,
            ["accessId"] = scope.AccessId == GroupAccess.Owner ? "owner" : "member",
            ["justification"] = request.Justification,
            ["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = GraphJson.EncoderDateString(request.StartDateTime ?? DateTimeOffset.UtcNow),
                ["expiration"] = new JsonObject
                {
                    ["type"] = "afterDuration",
                    ["duration"] = Iso8601Duration.Format(request.Duration),
                },
            },
        };

        if (request.Ticket is { } ticket)
        {
            body["ticketInfo"] = new JsonObject
            {
                ["ticketNumber"] = ticket.Number,
                ["ticketSystem"] = ticket.System,
            };
        }

        var response = await _transport.PostAsync(
            identity, tenantId, _transport.GraphUrl(Base + "/assignmentScheduleRequests"),
            Scopes, Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);

        var created = JsonSerializer.Deserialize<ScheduleRequest>(response.Body, GraphJson.Options)
            ?? throw new PimException(PimErrorKind.Unexpected, "Empty response body");

        var start = ScheduleRules.Effective(created.ScheduleInfo?.StartDateTime, request.StartDateTime);
        var end = ScheduleRules.End(
            created.ScheduleInfo?.Expiration?.EndDateTime, created.ScheduleInfo?.Expiration?.Duration,
            start, request.Duration);

        // A future start only masks an outcome that would otherwise read as active; pending/failed still win.
        var reported = Status(created.Status);
        var status = reported == AssignmentStatus.Active && ScheduleRules.IsFuture(start)
            ? AssignmentStatus.Scheduled
            : reported;

        return new ActiveAssignment(
            request.RoleKey, created.Id, start,
            status == AssignmentStatus.Active || status == AssignmentStatus.Scheduled ? end : null,
            status);
    }

    public async Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.RoleKey.Scope is not GroupScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var tenantId = assignment.RoleKey.TenantId;
        var principal = await RequestPrincipalIdAsync(scope.GroupId, scope.AccessId, identity, tenantId, ct)
            .ConfigureAwait(false);

        var body = new JsonObject
        {
            ["action"] = "selfDeactivate",
            ["principalId"] = principal,
            ["groupId"] = scope.GroupId,
            ["accessId"] = scope.AccessId == GroupAccess.Owner ? "owner" : "member",
        };

        await _transport.PostAsync(
            identity, tenantId, _transport.GraphUrl(Base + "/assignmentScheduleRequests"),
            Scopes, Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);
    }

    /// <summary>Withdraws a request still awaiting approval. Graph answers 204 with no body.</summary>
    public async Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.AssignmentId is not { } requestId)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        await _transport.PostAsync(
            identity, assignment.RoleKey.TenantId,
            _transport.GraphUrl($"{Base}/assignmentScheduleRequests/{requestId}/cancel"),
            Scopes, [], ct).ConfigureAwait(false);
    }
}
