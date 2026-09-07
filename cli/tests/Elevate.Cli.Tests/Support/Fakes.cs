using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;

namespace Elevate.Cli.Tests.Support;

/// <summary>A token provider that signs in whoever it is told to and never prompts. Port of the Core tests' fake.</summary>
public sealed class FakeTokenProvider : ITokenProvider
{
    private readonly List<Identity> _identities = [];

    public Identity NextSignIn { get; set; } = new("new", "new@x", "New", "home");

    public PimException? SilentError { get; set; }

    public List<string> InteractiveTenants { get; } = [];

    public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct = default)
    {
        var identity = NextSignIn with { SignInMethod = method };
        _identities.Add(identity);
        return Task.FromResult(identity);
    }

    public Task SignOutAsync(Identity identity, CancellationToken ct = default)
    {
        _identities.RemoveAll(i => i.Id == identity.Id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Identity>>([.. _identities]);

    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default) =>
        SilentError is { } e ? Task.FromException<string>(e) : Task.FromResult("token");

    public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct = default)
    {
        InteractiveTenants.Add(tenantId);
        SilentError = null;
        return Task.FromResult("token");
    }
}

/// <summary>A PIM provider whose eligible roles, assignments and failures the test scripts.</summary>
public sealed class FakeProvider(RoleScopeKind kind) : IPimProvider
{
    public RoleScopeKind Kind { get; } = kind;

    public IReadOnlyList<string> Scopes { get; } = ["scope"];

    public List<EligibleRole> Eligible { get; } = [];

    public List<ActiveAssignment> Assignments { get; } = [];

    public List<ActivationRequest> Activated { get; } = [];

    public List<ActiveAssignment> Deactivated { get; } = [];

    public List<ActiveAssignment> Cancelled { get; } = [];

    public Queue<PimException> Failures { get; } = new();

    public PimException? EligibleError { get; set; }

    public Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(Identity identity, TenantContext tenant, CancellationToken ct = default) =>
        EligibleError is { } e
            ? Task.FromException<IReadOnlyList<EligibleRole>>(e)
            : Task.FromResult<IReadOnlyList<EligibleRole>>([.. Eligible.Where(r => r.Key.TenantKey == tenant.Key)]);

    public Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(Identity identity, TenantContext tenant, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ActiveAssignment>>([.. Assignments.Where(a => a.RoleKey.TenantKey == tenant.Key)]);

    public Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default) =>
        Task.FromResult(role.Policy);

    public Task<ActiveAssignment> ActivateAsync(ActivationRequest request, Identity identity, CancellationToken ct = default)
    {
        if (Failures.Count > 0)
        {
            return Task.FromException<ActiveAssignment>(Failures.Dequeue());
        }

        Activated.Add(request);
        var now = DateTimeOffset.UtcNow;
        var assignment = request.Justification == "approve-me"
            ? new ActiveAssignment(request.RoleKey, "p", now, null, AssignmentStatus.PendingApproval)
            : new ActiveAssignment(request.RoleKey, "a", now, now + request.Duration, AssignmentStatus.Active);
        Assignments.RemoveAll(a => a.RoleKey == request.RoleKey);
        Assignments.Add(assignment);
        return Task.FromResult(assignment);
    }

    public Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        if (Failures.Count > 0)
        {
            return Task.FromException(Failures.Dequeue());
        }

        Deactivated.Add(assignment);
        Assignments.RemoveAll(a => a.RoleKey == assignment.RoleKey);
        return Task.CompletedTask;
    }

    public Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        Cancelled.Add(assignment);
        Assignments.RemoveAll(a => a.RoleKey == assignment.RoleKey);
        return Task.CompletedTask;
    }
}

public sealed class FakeApprovalProvider(RoleScopeKind kind) : IApprovalProvider
{
    public RoleScopeKind Kind { get; } = kind;

    public IReadOnlyList<string> Scopes { get; } = ["scope"];

    public List<ApprovalRequest> Pending { get; } = [];

    public List<(string Id, bool Approve, string Justification)> Decisions { get; } = [];

    public Task<IReadOnlyList<ApprovalRequest>> PendingApprovalsAsync(Identity identity, TenantContext tenant, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalRequest>>([.. Pending.Where(p => p.TenantKey == tenant.Key)]);

    public Task DecideAsync(ApprovalRequest request, bool approve, string justification, Identity identity, CancellationToken ct = default)
    {
        Decisions.Add((request.Id, approve, justification));
        Pending.RemoveAll(p => p.Id == request.Id);
        return Task.CompletedTask;
    }
}

/// <summary>An HTTP client that answers nothing; the session under test talks to fakes, never the network.</summary>
public sealed class NoHttpClient : IHttpClient
{
    public Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct) =>
        Task.FromException<HttpResponseData>(new PimException(PimErrorKind.Network, "no network in tests"));
}
