using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>PIM for Entra directory roles through Microsoft Graph. Port of the Swift <c>EntraDirectoryProvider</c>.</summary>
public sealed class EntraDirectoryProvider : IPimProvider
{
    private readonly GraphTransport _transport;

    public EntraDirectoryProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public RoleScopeKind Kind => RoleScopeKind.EntraDirectory;

    public IReadOnlyList<string> Scopes => Auth.Scopes.GraphAll;

    // MARK: Wire models

    private sealed record RoleDefinitionRef(string Id, string? DisplayName);

    private sealed record Schedule(
        string Id,
        string RoleDefinitionId,
        string? DirectoryScopeId,
        string? AssignmentType,
        DateTimeOffset? StartDateTime,
        DateTimeOffset? EndDateTime,
        RoleDefinitionRef? RoleDefinition,
        string? MemberType);

    private sealed record ScheduleRequest(
        string Id,
        string Status,
        string RoleDefinitionId,
        string? DirectoryScopeId,
        DateTimeOffset? CreatedDateTime,
        ScheduleInfo? ScheduleInfo);

    private sealed record Collection<T>(IReadOnlyList<T> Value);

    private sealed record Me(string Id);

    private static T Decode<T>(HttpResponseData response) =>
        JsonSerializer.Deserialize<T>(response.Body, GraphJson.Options)
        ?? throw new PimException(PimErrorKind.Unexpected, "Empty response body");

    // MARK: Reads

    public async Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var items = await _transport.ListAllAsync<Schedule>(
            identity, tenant.TenantId,
            _transport.GraphUrl("/roleManagement/directory/roleEligibilitySchedules/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
            Scopes, ct).ConfigureAwait(false);
        var seen = new HashSet<RoleScope>();
        var roles = new List<EligibleRole>();
        foreach (var s in items)
        {
            var scope = new EntraDirectoryScope(s.RoleDefinitionId, s.DirectoryScopeId ?? "/");
            if (!seen.Add(scope))
            {
                continue;
            }

            var key = new RoleKey(identity.Id, tenant.TenantId, scope);
            var viaGroup = string.Equals(s.MemberType, "Group", StringComparison.OrdinalIgnoreCase) ? "group" : null;
            roles.Add(new EligibleRole(
                key, s.RoleDefinition?.DisplayName ?? s.RoleDefinitionId,
                RoleSource.Discovered, RolePolicy.ManualDefault, ViaGroup: viaGroup));
        }

        return [.. roles.OrderBy(r => r.DisplayName, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var instances = await _transport.ListAllAsync<Schedule>(
            identity, tenant.TenantId,
            _transport.GraphUrl("/roleManagement/directory/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
            Scopes, ct).ConfigureAwait(false);

        // Widened past PendingApproval so a booked-ahead request, which the service has already
        // turned into a schedule, is the source for the scheduled rows below.
        var all = await _transport.ListAllAsync<ScheduleRequest>(
            identity, tenant.TenantId,
            _transport.GraphUrl("/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval' or status eq 'ScheduleCreated' or status eq 'Provisioned'"),
            Scopes, ct).ConfigureAwait(false);

        var activated = instances.Where(s => s.AssignmentType == "Activated");

        var result = new Dictionary<RoleKey, ActiveAssignment>();
        foreach (var s in activated)
        {
            var key = new RoleKey(identity.Id, tenant.TenantId, new EntraDirectoryScope(s.RoleDefinitionId, s.DirectoryScopeId ?? "/"));
            result[key] = new ActiveAssignment(
                key, s.Id, s.StartDateTime ?? DateTimeOffset.UtcNow, s.EndDateTime, AssignmentStatus.Active);
        }

        foreach (var p in all.Where(r => r.Status == "PendingApproval"))
        {
            var key = new RoleKey(identity.Id, tenant.TenantId, new EntraDirectoryScope(p.RoleDefinitionId, p.DirectoryScopeId ?? "/"));
            if (result.ContainsKey(key))
            {
                continue;
            }

            result[key] = new ActiveAssignment(
                key, p.Id,
                p.ScheduleInfo?.StartDateTime ?? p.CreatedDateTime ?? DateTimeOffset.UtcNow,
                null, AssignmentStatus.PendingApproval);
        }

        foreach (var u in all.Where(r => !ScheduleRules.IsSettledOrPending(r.Status)))
        {
            if (u.ScheduleInfo?.StartDateTime is not { } start || !ScheduleRules.IsFuture(start))
            {
                continue;
            }

            var key = new RoleKey(identity.Id, tenant.TenantId, new EntraDirectoryScope(u.RoleDefinitionId, u.DirectoryScopeId ?? "/"));
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

        if (role.Key.Scope is not EntraDirectoryScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var filter = $"scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '{scope.RoleDefinitionId}'";
        var encoded = GraphTransport.PercentEncodeQuery(filter);
        var response = await _transport.GetAsync(
            identity, role.Key.TenantId,
            _transport.GraphUrl($"/policies/roleManagementPolicyAssignments?$filter={encoded}&$expand=policy($expand=rules)"),
            Scopes, ct).ConfigureAwait(false);

        var assignments = Decode<Collection<PolicyAssignment>>(response).Value;
        var rules = assignments.Count > 0 ? assignments[0].Policy?.Rules : null;
        return rules is null ? RolePolicy.ManualDefault : PolicyRules.Apply(rules);
    }

    // MARK: Activate / deactivate

    private async Task<string> PrincipalIdAsync(Identity identity, string tenantId, CancellationToken ct)
    {
        var response = await _transport.GetAsync(identity, tenantId, _transport.GraphUrl("/me?$select=id"), Scopes, ct)
            .ConfigureAwait(false);
        return Decode<Me>(response).Id;
    }

    public async Task<ActiveAssignment> ActivateAsync(
        ActivationRequest request, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RoleKey.Scope is not EntraDirectoryScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var principal = await PrincipalIdAsync(identity, request.RoleKey.TenantId, ct).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["action"] = "selfActivate",
            ["principalId"] = principal,
            ["roleDefinitionId"] = scope.RoleDefinitionId,
            ["directoryScopeId"] = scope.DirectoryScopeId,
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
            identity, request.RoleKey.TenantId,
            _transport.GraphUrl("/roleManagement/directory/roleAssignmentScheduleRequests"),
            Scopes, System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);

        var created = Decode<ScheduleRequest>(response);
        var start = ScheduleRules.Effective(created.ScheduleInfo?.StartDateTime, request.StartDateTime);
        var end = ScheduleRules.End(
            created.ScheduleInfo?.Expiration?.EndDateTime, created.ScheduleInfo?.Expiration?.Duration,
            start, request.Duration);

        var (status, reportedEnd) = GraphSchedule.Settle(created.Status, start, end);
        return new ActiveAssignment(request.RoleKey, created.Id, start, reportedEnd, status);
    }

    public async Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.RoleKey.Scope is not EntraDirectoryScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var principal = await PrincipalIdAsync(identity, assignment.RoleKey.TenantId, ct).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["action"] = "selfDeactivate",
            ["principalId"] = principal,
            ["roleDefinitionId"] = scope.RoleDefinitionId,
            ["directoryScopeId"] = scope.DirectoryScopeId,
        };

        await _transport.PostAsync(
            identity, assignment.RoleKey.TenantId,
            _transport.GraphUrl("/roleManagement/directory/roleAssignmentScheduleRequests"),
            Scopes, System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);
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
            _transport.GraphUrl($"/roleManagement/directory/roleAssignmentScheduleRequests/{requestId}/cancel"),
            Scopes, [], ct).ConfigureAwait(false);
    }
}
