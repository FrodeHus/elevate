using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>PropagationWatcherTests</c>.</summary>
public class PropagationWatcherTests
{
    private static readonly Identity Me = new("id1", "u@contoso.com", "U", "t-home");

    private static ActiveAssignment Active(RoleScope scope) =>
        new(new RoleKey("id1", "t1", scope), "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

    private static readonly RoleScope EntraScope = new EntraDirectoryScope("role-1", "/");
    private static readonly RoleScope GroupScopeValue = new GroupScope("grp-1", GroupAccess.Member);

    /// <summary>A provider whose probe answers from a queue, so a test can make propagation take a few rounds.</summary>
    private sealed class ProbeProvider(RoleScopeKind kind, params EffectiveAccess[] answers) : IPimProvider
    {
        private readonly Queue<EffectiveAccess> _answers = new(answers);

        public int Probes { get; private set; }

        public RoleScopeKind Kind => kind;

        public IReadOnlyList<string> Scopes => [];

        public Exception? Throws { get; set; }

        public Task<EffectiveAccess> EffectiveAccessAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
        {
            Probes++;
            if (Throws is { } error)
            {
                return Task.FromException<EffectiveAccess>(error);
            }

            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : EffectiveAccess.NotYet);
        }

        public Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(Identity identity, TenantContext tenant, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EligibleRole>>([]);

        public Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(Identity identity, TenantContext tenant, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActiveAssignment>>([]);

        public Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default)
            => Task.FromResult(RolePolicy.ManualDefault);

        public Task<ActiveAssignment> ActivateAsync(ActivationRequest request, Identity identity, CancellationToken ct = default)
            => Task.FromException<ActiveAssignment>(new PimException(PimErrorKind.NotEligible));

        public Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default) => Task.CompletedTask;

        public Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Advances a virtual clock instead of sleeping, so the tests run in no time.</summary>
    private sealed class Clock
    {
        public TimeSpan Elapsed { get; private set; }

        public Task DelayAsync(TimeSpan span, CancellationToken ct)
        {
            Elapsed += span;
            return Task.CompletedTask;
        }
    }

    private static PropagationWatcher Watcher(Clock clock, params IPimProvider[] providers) =>
        new(new ActivationCoordinator(providers, new FakeTokenProvider()), clock.DelayAsync);

    [Fact]
    public async Task ReadyOnceTheProbeConfirms()
    {
        var clock = new Clock();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory, EffectiveAccess.NotYet, EffectiveAccess.Confirmed);

        var outcomes = await Watcher(clock, provider).WaitAsync([Active(EntraScope)], [Me]);

        outcomes.Should().ContainSingle().Which.State.Should().Be(PropagationState.Ready);
        provider.Probes.Should().Be(2);
    }

    [Fact]
    public async Task ReportsEachRoleAsItSettlesRatherThanOnlyAtTheEnd()
    {
        var clock = new Clock();
        var reported = new List<PropagationOutcome>();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory, EffectiveAccess.Confirmed);

        await Watcher(clock, provider).WaitAsync([Active(EntraScope)], [Me], null, reported.Add);

        reported.Should().ContainSingle().Which.State.Should().Be(PropagationState.Ready);
    }

    [Fact]
    public async Task UnconfirmedOnceTheDeadlinePasses()
    {
        var clock = new Clock();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory);

        var outcomes = await Watcher(clock, provider)
            .WaitAsync([Active(EntraScope)], [Me], TimeSpan.Zero);

        var outcome = outcomes.Should().ContainSingle().Subject;
        outcome.State.Should().Be(PropagationState.Unconfirmed);
        // The user is told what usually explains it, not just that it did not happen.
        outcome.Detail.Should().Be(PropagationHints.LikelyCause(RoleScopeKind.EntraDirectory));
    }

    [Fact]
    public async Task UnobservableStopsProbingStraightAway()
    {
        var clock = new Clock();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory, EffectiveAccess.Unknown("scoped role"));

        var outcomes = await Watcher(clock, provider).WaitAsync([Active(EntraScope)], [Me]);

        outcomes.Should().ContainSingle().Which.State.Should().Be(PropagationState.Unobservable);
        provider.Probes.Should().Be(1);
    }

    [Fact]
    public async Task AFailedProbeIsUnobservable_NotAFailedActivation()
    {
        var clock = new Clock();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory) { Throws = new PimException(PimErrorKind.Network, "offline") };

        var outcomes = await Watcher(clock, provider).WaitAsync([Active(EntraScope)], [Me]);

        outcomes.Should().ContainSingle().Which.State.Should().Be(PropagationState.Unobservable);
    }

    [Fact]
    public async Task OneUnobservableRoleDoesNotHoldUpTheOthers()
    {
        var clock = new Clock();
        var entra = new ProbeProvider(RoleScopeKind.EntraDirectory, EffectiveAccess.Unknown("scoped role"));
        var group = new ProbeProvider(RoleScopeKind.Group, EffectiveAccess.NotYet, EffectiveAccess.Confirmed);

        var outcomes = await Watcher(clock, entra, group)
            .WaitAsync([Active(EntraScope), Active(GroupScopeValue)], [Me]);

        outcomes.Should().HaveCount(2);
        outcomes.Single(o => o.RoleKey.Scope.Kind == RoleScopeKind.EntraDirectory).State
            .Should().Be(PropagationState.Unobservable);
        outcomes.Single(o => o.RoleKey.Scope.Kind == RoleScopeKind.Group).State
            .Should().Be(PropagationState.Ready);
        // The Entra role was asked once and then left alone.
        entra.Probes.Should().Be(1);
    }

    [Fact]
    public async Task OnlyActiveAssignmentsAreProbed()
    {
        var clock = new Clock();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory, EffectiveAccess.Confirmed);
        var pending = new ActiveAssignment(
            new RoleKey("id1", "t1", EntraScope), "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.PendingApproval);

        var outcomes = await Watcher(clock, provider).WaitAsync([pending], [Me]);

        outcomes.Should().BeEmpty();
        provider.Probes.Should().Be(0);
    }

    [Fact]
    public async Task AnAccountThatIsNotSignedInHereIsUnobservable()
    {
        var clock = new Clock();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory, EffectiveAccess.Confirmed);

        var outcomes = await Watcher(clock, provider).WaitAsync([Active(EntraScope)], []);

        outcomes.Should().ContainSingle().Which.State.Should().Be(PropagationState.Unobservable);
        provider.Probes.Should().Be(0);
    }

    [Fact]
    public async Task ARoleWithNoProviderIsUnobservable()
    {
        var clock = new Clock();
        var outcomes = await Watcher(clock, new ProbeProvider(RoleScopeKind.Group, EffectiveAccess.Confirmed))
            .WaitAsync([Active(EntraScope)], [Me]);

        outcomes.Should().ContainSingle().Which.State.Should().Be(PropagationState.Unobservable);
    }

    [Fact]
    public async Task ProbingWaitsBeforeTheFirstCallAndThenSettlesOnTheLongerInterval()
    {
        var clock = new Clock();
        var provider = new ProbeProvider(RoleScopeKind.EntraDirectory, EffectiveAccess.NotYet, EffectiveAccess.Confirmed);
        var watcher = Watcher(clock, provider);

        await watcher.WaitAsync([Active(EntraScope)], [Me]);

        clock.Elapsed.Should().Be(watcher.FirstInterval + watcher.MaxInterval);
    }
}
