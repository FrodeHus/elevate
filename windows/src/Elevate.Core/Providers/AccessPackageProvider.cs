using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>Self-service entitlement management for the signed-in user. Port of the Swift <c>AccessPackageProvider</c>.</summary>
public interface IAccessPackageProvider
{
    IReadOnlyList<string> Scopes { get; }

    /// <summary>Every package the caller may request, hidden ones included but flagged.</summary>
    Task<IReadOnlyList<AccessPackage>> RequestablePackagesAsync(Identity identity, string tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<AccessPackageRequest>> MyRequestsAsync(Identity identity, string tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<AccessPackageAssignment>> MyAssignmentsAsync(Identity identity, string tenantId, CancellationToken ct = default);

    /// <summary>One entry per policy the caller may request <paramref name="packageId"/> under.</summary>
    Task<IReadOnlyList<PolicyRequirement>> RequirementsAsync(string packageId, Identity identity, string tenantId, CancellationToken ct = default);

    /// <summary>Submits a <c>userAdd</c> request. <paramref name="policyId"/> is required by Graph only when several policies apply.</summary>
    Task<AccessPackageRequest> RequestAsync(string packageId, string? policyId, string justification, Identity identity, string tenantId, CancellationToken ct = default);

    Task CancelAsync(string requestId, Identity identity, string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Self-service entitlement management for the signed-in user: the access packages they may
/// request, their own requests and assignments. Graph v1.0 only.
/// </summary>
public sealed class AccessPackageProvider : IAccessPackageProvider
{
    internal const string Base = "/identityGovernance/entitlementManagement";

    private readonly GraphTransport _transport;

    public AccessPackageProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public IReadOnlyList<string> Scopes => Auth.Scopes.EntitlementAll;

    // MARK: Wire shapes

    private sealed record PackageDto(string Id, string? DisplayName, string? Description, bool? IsHidden);

    private sealed record AssignmentRef(string? Id, string? AccessPackageId, string? AssignmentPolicyId);

    private sealed record RequestDto(
        string Id,
        string? RequestType,
        string? State,
        string? Status,
        string? Justification,
        DateTimeOffset? CreatedDateTime,
        DateTimeOffset? CompletedDateTime,
        GraphApprovals.Named? AccessPackage,
        AssignmentRef? Assignment);

    private sealed record AssignmentDto(
        string Id,
        string? State,
        ScheduleInfo? Schedule,
        GraphApprovals.Named? AccessPackage,
        GraphApprovals.Named? AssignmentPolicy);

    /// <summary>Only whether a policy asks questions matters, so the questions themselves are not modelled.</summary>
    private sealed record RequirementDto(
        string? PolicyId,
        string? PolicyDisplayName,
        string? PolicyDescription,
        bool? IsApprovalRequired,
        IReadOnlyList<JsonElement>? Questions);

    private static AccessPackageRequest Request(RequestDto r) => new(
        r.Id,
        r.AccessPackage?.Id ?? r.Assignment?.AccessPackageId ?? string.Empty,
        r.AccessPackage?.DisplayName ?? r.Id,
        r.RequestType ?? "userAdd",
        AccessPackageRequestStates.Parse(r.State),
        r.Status,
        r.Justification,
        r.CreatedDateTime,
        r.CompletedDateTime,
        r.Assignment?.AssignmentPolicyId);

    private static AccessPackageAssignment Assignment(AssignmentDto a) => new(
        a.Id,
        a.AccessPackage?.Id ?? string.Empty,
        a.AccessPackage?.DisplayName ?? a.Id,
        AccessPackageAssignmentStates.Parse(a.State),
        a.AssignmentPolicy?.DisplayName,
        ExpiresAt(a.Schedule));

    /// <summary>An assignment's end, whether Graph sent it as a date or as a duration from the start.</summary>
    private static DateTimeOffset? ExpiresAt(ScheduleInfo? schedule) =>
        schedule?.Expiration is { } expiration && schedule.StartDateTime is { } start
            ? ScheduleRules.End(expiration.EndDateTime, expiration.Duration, start)
            : schedule?.Expiration?.EndDateTime;

    // MARK: Reads

    public async Task<IReadOnlyList<AccessPackage>> RequestablePackagesAsync(Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl(Base + "/accessPackages/filterByCurrentUser(on='allowedRequestor')");
        var items = await _transport.ListAllAsync<PackageDto>(identity, tenantId, url, Scopes, ct).ConfigureAwait(false);
        return [.. items.Select(p => new AccessPackage(p.Id, p.DisplayName ?? p.Id, p.Description, p.IsHidden ?? false))];
    }

    public async Task<IReadOnlyList<AccessPackageRequest>> MyRequestsAsync(Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl(Base + "/assignmentRequests/filterByCurrentUser(on='target')?$expand=accessPackage,assignment");
        var items = await _transport.ListAllAsync<RequestDto>(identity, tenantId, url, Scopes, ct).ConfigureAwait(false);
        return [.. items.Select(Request)];
    }

    public async Task<IReadOnlyList<AccessPackageAssignment>> MyAssignmentsAsync(Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl(Base + "/assignments/filterByCurrentUser(on='target')?$expand=accessPackage,assignmentPolicy");
        var items = await _transport.ListAllAsync<AssignmentDto>(identity, tenantId, url, Scopes, ct).ConfigureAwait(false);
        return [.. items.Select(Assignment)];
    }

    // MARK: Requirements and requests

    public async Task<IReadOnlyList<PolicyRequirement>> RequirementsAsync(string packageId, Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl($"{Base}/accessPackages/{packageId}/getApplicablePolicyRequirements");
        var response = await _transport.PostAsync(identity, tenantId, url, Scopes, [], ct).ConfigureAwait(false);
        var page = JsonSerializer.Deserialize<GraphTransport.Page<RequirementDto>>(response.Body, GraphJson.Options);
        return
        [
            .. (page?.Value ?? [])
                .Where(d => d.PolicyId is not null)
                .Select(d => new PolicyRequirement(
                    d.PolicyId!, d.PolicyDisplayName ?? d.PolicyId!, d.PolicyDescription,
                    d.IsApprovalRequired ?? false, (d.Questions?.Count ?? 0) > 0)),
        ];
    }

    public async Task<AccessPackageRequest> RequestAsync(string packageId, string? policyId, string justification, Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentNullException.ThrowIfNull(justification);
        ArgumentNullException.ThrowIfNull(identity);
        var assignment = new JsonObject { ["accessPackageId"] = packageId };
        if (policyId is not null)
        {
            assignment["assignmentPolicyId"] = policyId;
        }

        var body = new JsonObject
        {
            ["requestType"] = "userAdd",
            ["justification"] = justification,
            ["assignment"] = assignment,
        };
        var response = await _transport.PostAsync(
            identity, tenantId, _transport.GraphUrl(Base + "/assignmentRequests"),
            Scopes, Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<RequestDto>(response.Body, GraphJson.Options)
            ?? throw new PimException(PimErrorKind.Unexpected, "Empty response body");
        var created = Request(dto);
        return created.PackageId.Length == 0 ? created with { PackageId = packageId } : created;
    }

    public async Task CancelAsync(string requestId, Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(identity);
        await _transport.PostAsync(
            identity, tenantId, _transport.GraphUrl($"{Base}/assignmentRequests/{requestId}/cancel"), Scopes, [], ct).ConfigureAwait(false);
    }

    /// <summary>The My Access portal page for one package, for policies whose questions Elevate does not collect.</summary>
    public static Uri MyAccessUrl(string tenantId, string packageId) =>
        new($"https://myaccess.microsoft.com/@{tenantId}#/access-packages/{packageId}");
}
