using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Core.Tests.Support;

/// <summary>
/// Scriptable provider. <see cref="PushFailure"/> queues errors that are consumed one per call, in
/// order, before the call succeeds. Port of the Swift <c>FakeProvider</c>.
/// </summary>
public sealed class FakeProvider : IPimProvider
{
    private readonly Lock _gate = new();
    private readonly Queue<PimException> _failures = new();
    private readonly List<ActivationRequest> _activated = [];
    private readonly List<ActiveAssignment> _deactivated = [];
    private readonly List<ActiveAssignment> _cancelled = [];
    private readonly List<string> _order = [];

    public FakeProvider(RoleScopeKind kind) => Kind = kind;

    public RoleScopeKind Kind { get; }

    public IReadOnlyList<string> Scopes { get; } = ["scope"];

    public IReadOnlyList<ActivationRequest> Activated
    {
        get { lock (_gate) { return [.. _activated]; } }
    }

    public IReadOnlyList<ActiveAssignment> Deactivated
    {
        get { lock (_gate) { return [.. _deactivated]; } }
    }

    public IReadOnlyList<ActiveAssignment> Cancelled
    {
        get { lock (_gate) { return [.. _cancelled]; } }
    }

    /// <summary>"{tenantId}:{justification}" for each activation, in the order they ran.</summary>
    public IReadOnlyList<string> Order
    {
        get { lock (_gate) { return [.. _order]; } }
    }

    public void PushFailure(PimException error)
    {
        lock (_gate)
        {
            _failures.Enqueue(error);
        }
    }

    private PimException? NextFailure()
    {
        lock (_gate)
        {
            return _failures.Count > 0 ? _failures.Dequeue() : null;
        }
    }

    public Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(Identity identity, TenantContext tenant, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EligibleRole>>([]);

    public Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(Identity identity, TenantContext tenant, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActiveAssignment>>([]);

    public Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default)
        => Task.FromResult(RolePolicy.ManualDefault);

    public Task<ActiveAssignment> ActivateAsync(ActivationRequest request, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (NextFailure() is { } error)
        {
            return Task.FromException<ActiveAssignment>(error);
        }

        lock (_gate)
        {
            _activated.Add(request);
            _order.Add($"{request.RoleKey.TenantId}:{request.Justification}");
        }

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(request.Justification == "approve-me"
            ? new ActiveAssignment(request.RoleKey, "p", now, null, AssignmentStatus.PendingApproval)
            : new ActiveAssignment(request.RoleKey, "a", now, now + request.Duration, AssignmentStatus.Active));
    }

    public Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        if (NextFailure() is { } error)
        {
            return Task.FromException(error);
        }

        lock (_gate)
        {
            _deactivated.Add(assignment);
        }

        return Task.CompletedTask;
    }

    public Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        if (NextFailure() is { } error)
        {
            return Task.FromException(error);
        }

        lock (_gate)
        {
            _cancelled.Add(assignment);
        }

        return Task.CompletedTask;
    }
}
