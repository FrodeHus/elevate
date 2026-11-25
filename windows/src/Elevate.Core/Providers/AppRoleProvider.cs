using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>
/// PIM for app roles (preview) through Microsoft Graph beta: eligibility for, and activation of, an
/// enterprise application's app role. The API mirrors PIM for Groups under <c>/appRole</c>.
/// Port of the Swift <c>AppRoleProvider</c>.
/// </summary>
public sealed class AppRoleProvider : IPimProvider
{
    /// <summary>Only on the beta endpoint while the feature is in preview.</summary>
    internal const string Base = "/identityGovernance/privilegedAccess/appRole";

    private const string EligibilityPath =
        Base + "/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=servicePrincipal($select=id,displayName)";

    private readonly GraphTransport _transport;

    public AppRoleProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public RoleScopeKind Kind => RoleScopeKind.AppRole;

    public IReadOnlyList<string> Scopes => Auth.Scopes.AppRoleAll;

    // MARK: Wire models

    private sealed record AppRoleRef(string? DisplayName, string? Value);

    private sealed record ServicePrincipalRef(string? DisplayName);

    private sealed record Instance(
        string Id,
        string? PrincipalId,
        string ServicePrincipalId,
        string AppRoleId,
        string? MemberType,
        string? AssignmentType,
        string? AssignmentScheduleId,
        DateTimeOffset? StartDateTime,
        DateTimeOffset? EndDateTime,
        AppRoleRef? AppRole,
        ServicePrincipalRef? ServicePrincipal);

    private sealed record ScheduleRequest(
        string Id,
        string Status,
        string ServicePrincipalId,
        string AppRoleId,
        DateTimeOffset? CreatedDateTime,
        ScheduleInfo? ScheduleInfo,
        string? TargetScheduleId,
        string? Action);

    /// <summary>
    /// A tenant without the preview (no licence, or not rolled out) refuses these reads. That is the
    /// normal case, not a fault, so it reads as "no app roles" instead of an error on every refresh.
    /// Only a refusal counts: a 5xx or an unreadable answer is a failed read and must surface, or a
    /// passing fault would blank the user's app roles.
    /// </summary>
    private async Task<IReadOnlyList<T>> ListOrEmptyAsync<T>(Identity identity, string tenantId, string path, CancellationToken ct)
    {
        try
        {
            return await _transport.ListAllAsync<T>(identity, tenantId, _transport.GraphBetaUrl(path), Scopes, ct).ConfigureAwait(false);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.ConsentRequired or PimErrorKind.Forbidden
            || (e.Kind == PimErrorKind.Unexpected && e.Status is 400 or 404))
        {
            return [];
        }
    }

    // MARK: Reads

    public async Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var items = await ListOrEmptyAsync<Instance>(identity, tenant.TenantId, EligibilityPath, ct).ConfigureAwait(false);

        var seen = new HashSet<RoleScope>();
        var roles = new List<EligibleRole>();
        foreach (var i in items)
        {
            var scope = new AppRoleScope(i.ServicePrincipalId, i.AppRoleId);
            if (!seen.Add(scope))
            {
                continue;
            }

            roles.Add(new EligibleRole(
                new RoleKey(identity.Id, tenant.TenantId, scope),
                i.AppRole?.DisplayName ?? i.AppRole?.Value ?? i.AppRoleId,
                RoleSource.Discovered,
                RolePolicy.ManualDefault,
                Detail: i.ServicePrincipal?.DisplayName ?? i.ServicePrincipalId,
                ViaGroup: string.Equals(i.MemberType, "group", StringComparison.OrdinalIgnoreCase) ? "group" : null));
        }

        return
        [
            .. roles
                .OrderBy(r => r.Detail ?? "", StringComparer.Ordinal)
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal),
        ];
    }

    public async Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var instances = await ListOrEmptyAsync<Instance>(
            identity, tenant.TenantId, Base + "/assignmentScheduleInstances/filterByCurrentUser(on='principal')", ct).ConfigureAwait(false);
        var requests = await ListOrEmptyAsync<ScheduleRequest>(
            identity, tenant.TenantId,
            Base + "/assignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval' or status eq 'ScheduleCreated' or status eq 'Provisioned'",
            ct).ConfigureAwait(false);

        RoleKey Key(string servicePrincipalId, string appRoleId) =>
            new(identity.Id, tenant.TenantId, new AppRoleScope(servicePrincipalId, appRoleId));

        var result = new Dictionary<RoleKey, ActiveAssignment>();

        // A standing ("assigned") app role is not ours to deactivate, so only activations are listed.
        foreach (var i in instances.Where(i => string.Equals(i.AssignmentType, "activated", StringComparison.OrdinalIgnoreCase)))
        {
            var key = Key(i.ServicePrincipalId, i.AppRoleId);
            var originatingRequest = requests.FirstOrDefault(r =>
                i.AssignmentScheduleId is not null && r.TargetScheduleId == i.AssignmentScheduleId
                    && r.Action == "selfActivate");
            result[key] = new ActiveAssignment(
                key, i.Id, i.StartDateTime ?? DateTimeOffset.UtcNow, i.EndDateTime, AssignmentStatus.Active, i.AssignmentScheduleId, originatingRequest?.Id);
        }

        foreach (var r in requests.Where(r => r.Status == "PendingApproval"))
        {
            var key = Key(r.ServicePrincipalId, r.AppRoleId);
            if (result.ContainsKey(key))
            {
                continue;
            }

            result[key] = new ActiveAssignment(
                key, r.Id,
                r.ScheduleInfo?.StartDateTime ?? r.CreatedDateTime ?? DateTimeOffset.UtcNow,
                null, AssignmentStatus.PendingApproval, r.TargetScheduleId, r.Id);
        }

        foreach (var u in requests.Where(r => !ScheduleRules.IsSettledOrPending(r.Status)))
        {
            if (u.ScheduleInfo?.StartDateTime is not { } start || !ScheduleRules.IsFuture(start))
            {
                continue;
            }

            var key = Key(u.ServicePrincipalId, u.AppRoleId);
            if (result.ContainsKey(key))
            {
                continue;
            }

            var end = ScheduleRules.End(u.ScheduleInfo?.Expiration?.EndDateTime, u.ScheduleInfo?.Expiration?.Duration, start);
            result[key] = new ActiveAssignment(key, u.Id, start, end, AssignmentStatus.Scheduled, u.TargetScheduleId, u.Id);
        }

        return [.. result.Values];
    }

    // MARK: Policy

    private sealed record Policy(string Id, IReadOnlyList<PolicyRules.Rule>? Rules);

    private sealed record PolicyAssignment(string Id, string? RoleDefinitionId, Policy? Policy);

    public async Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (role.Key.Scope is not AppRoleScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var filter = $"scopeId eq '{GraphTransport.OdataEscaped(scope.ServicePrincipalId)}' and scopeType eq 'AppRole' and roleDefinitionId eq '{GraphTransport.OdataEscaped(scope.AppRoleId)}'";
        var encoded = GraphTransport.PercentEncodeQuery(filter);
        var response = await _transport.GetAsync(
            identity, role.Key.TenantId,
            _transport.GraphBetaUrl($"/policies/roleManagementPolicyAssignments?$filter={encoded}&$expand=policy($expand=rules)"),
            Scopes, ct).ConfigureAwait(false);

        var page = JsonSerializer.Deserialize<GraphTransport.Page<PolicyAssignment>>(response.Body, GraphJson.Options);
        var assignments = page?.Value ?? [];
        var rules = assignments.Count > 0 ? assignments[0].Policy?.Rules : null;
        return rules is null ? RolePolicy.ManualDefault : PolicyRules.Apply(rules);
    }

    // MARK: Activate / deactivate

    /// <summary>
    /// Always the caller: an eligibility inherited through a group names that group, which Graph refuses.
    /// The eligibility's own principal is used only when the token hides the caller's oid.
    /// </summary>
    private async Task<string> RequestPrincipalIdAsync(AppRoleScope scope, Identity identity, string tenantId, CancellationToken ct)
    {
        if (await _transport.CallerObjectIdAsync(identity, tenantId, Scopes, ct).ConfigureAwait(false) is { } oid)
        {
            return oid;
        }

        var items = await _transport.ListAllAsync<Instance>(
            identity, tenantId, _transport.GraphBetaUrl(EligibilityPath), Scopes, ct).ConfigureAwait(false);
        return items.FirstOrDefault(i => i.ServicePrincipalId == scope.ServicePrincipalId && i.AppRoleId == scope.AppRoleId)?.PrincipalId
            ?? throw new PimException(PimErrorKind.NotEligible);
    }

    public async Task<ActiveAssignment> ActivateAsync(
        ActivationRequest request, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RoleKey.Scope is not AppRoleScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var tenantId = request.RoleKey.TenantId;
        var principal = await RequestPrincipalIdAsync(scope, identity, tenantId, ct).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["action"] = "selfActivate",
            ["principalId"] = principal,
            ["servicePrincipalId"] = scope.ServicePrincipalId,
            ["appRoleId"] = scope.AppRoleId,
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
            identity, tenantId, _transport.GraphBetaUrl(Base + "/assignmentScheduleRequests"),
            Scopes, Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);

        var created = JsonSerializer.Deserialize<ScheduleRequest>(response.Body, GraphJson.Options)
            ?? throw new PimException(PimErrorKind.Unexpected, "Empty response body");

        var start = ScheduleRules.Effective(created.ScheduleInfo?.StartDateTime, request.StartDateTime);
        var end = ScheduleRules.End(
            created.ScheduleInfo?.Expiration?.EndDateTime, created.ScheduleInfo?.Expiration?.Duration,
            start, request.Duration);

        var (status, reportedEnd) = GraphSchedule.Settle(created.Status, start, end);
        return new ActiveAssignment(request.RoleKey, created.Id, start, reportedEnd, status, created.TargetScheduleId, created.Id);
    }

    public async Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.RoleKey.Scope is not AppRoleScope scope)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        var tenantId = assignment.RoleKey.TenantId;
        var principal = await RequestPrincipalIdAsync(scope, identity, tenantId, ct).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["action"] = "selfDeactivate",
            ["principalId"] = principal,
            ["servicePrincipalId"] = scope.ServicePrincipalId,
            ["appRoleId"] = scope.AppRoleId,
        };

        if (assignment.ScheduleId is { } scheduleId) body["targetScheduleId"] = scheduleId;

        var response = await _transport.PostAsync(
            identity, tenantId, _transport.GraphBetaUrl(Base + "/assignmentScheduleRequests"),
            Scopes, Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);
        var outcome = JsonSerializer.Deserialize<ScheduleRequest>(response.Body, GraphJson.Options)!.Status;
        if (outcome is not ("Provisioned" or "Revoked"))
            throw new PimException(PimErrorKind.Unexpected, $"Deactivation has not completed: {outcome ?? "Unknown"}");
    }

    /// <summary>
    /// An app role reaches the app as a <c>roles</c> claim in a token minted for that app, which
    /// Elevate never holds, so there is nothing here to observe.
    /// </summary>
    public Task<EffectiveAccess> EffectiveAccessAsync(
        ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
        => Task.FromResult(EffectiveAccess.Unknown(
            "An app role shows up in the application's own token, so Elevate cannot see when it takes effect."));

    /// <summary>Withdraws a request still awaiting approval.</summary>
    public async Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.AssignmentId is not { } requestId)
        {
            throw new PimException(PimErrorKind.NotEligible);
        }

        await _transport.PostAsync(
            identity, assignment.RoleKey.TenantId,
            _transport.GraphBetaUrl($"{Base}/assignmentScheduleRequests/{requestId}/cancel"),
            Scopes, [], ct).ConfigureAwait(false);
    }
}
