using System.Collections.Concurrent;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ActivationCoordinatorTests
{
    private static readonly Identity Account = new("id1", "u@x", "U", "t1");

    private static RoleKey Key(string tenant, string role) =>
        new("id1", tenant, new EntraDirectoryScope(role, "/"));

    private static ActivationRequest Request(string tenant, string role, string justification = "j", string? context = null) =>
        new(Key(tenant, role), TimeSpan.FromMinutes(1), justification, AuthenticationContext: context);

    [Fact]
    public async Task ActivatesSingleRole()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        var coordinator = new ActivationCoordinator([provider], new FakeTokenProvider());

        var outcomes = await coordinator.ActivateAsync(
            [new ActivationRequest(Key("t1", "r1"), TimeSpan.FromHours(1), "j")], [Account]);

        outcomes.Should().ContainSingle();
        outcomes[0].Result.Should().BeOfType<ActivationResult.Activated>()
            .Which.Assignment.Status.Should().Be(AssignmentStatus.Active);
        provider.Activated.Should().ContainSingle();
    }

    [Fact]
    public void ProviderLookupExposesTheRegisteredProviders()
    {
        var provider = new FakeProvider(RoleScopeKind.Group);
        var coordinator = new ActivationCoordinator([provider], new FakeTokenProvider());

        coordinator.Provider(RoleScopeKind.Group).Should().BeSameAs(provider);
        coordinator.Provider(RoleScopeKind.AzureResource).Should().BeNull();
    }

    [Fact]
    public async Task BulkRunsSequentiallyWithinTenantAndReportsProgress()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        var coordinator = new ActivationCoordinator([provider], new FakeTokenProvider());
        var requests = new[] { Request("t1", "r1", "a"), Request("t1", "r2", "b"), Request("t2", "r1", "c") };
        var progress = new ConcurrentBag<ActivationOutcome>();

        var outcomes = await coordinator.ActivateAsync(requests, [Account], progress.Add);

        outcomes.Should().HaveCount(3);
        outcomes.Select(o => o.RoleKey).Should().BeEquivalentTo(requests.Select(r => r.RoleKey));
        var order = provider.Order.ToList();
        order.IndexOf("t1:a").Should().BeLessThan(order.IndexOf("t1:b"));
        progress.Should().HaveCount(3);
    }

    [Fact]
    public async Task ClaimsChallengeTriggersInteractiveAndRetries()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.ClaimsChallenge, """{"access_token":{}}"""));
        var tokens = new FakeTokenProvider();
        var coordinator = new ActivationCoordinator([provider], tokens);

        var outcomes = await coordinator.ActivateAsync([Request("t1", "r1")], [Account]);

        outcomes[0].Result.Should().BeOfType<ActivationResult.Activated>();
        tokens.InteractiveCalls.Should().ContainSingle();
        tokens.InteractiveCalls[0].TenantId.Should().Be("t1");
        tokens.InteractiveCalls[0].Claims.Should().Be("""{"access_token":{}}""");
        tokens.InteractiveCalls[0].Scopes.Should().Equal("scope");
    }

    [Fact]
    public async Task SecondFailureIsReportedNotRetriedForever()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.ClaimsChallenge, "{}"));
        provider.PushFailure(new PimException(PimErrorKind.ClaimsChallenge, "{}"));
        var coordinator = new ActivationCoordinator([provider], new FakeTokenProvider());

        var outcomes = await coordinator.ActivateAsync([Request("t1", "r1")], [Account]);

        var error = outcomes[0].Result.Should().BeOfType<ActivationResult.Failed>().Which.Error;
        error.Kind.Should().Be(PimErrorKind.PolicyViolation);
        error.Detail.Should().Contain("multi-factor authentication");
    }

    /// <summary>
    /// A PIM MFA rule comes back as a 400 with no claims header, i.e. interaction required; the retry
    /// must ask the browser for an MFA re-verification, not a silent SSO round trip.
    /// </summary>
    [Fact]
    public async Task MfaRuleRetriesWithMultiFactorClaims()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.InteractionRequired));
        var tokens = new FakeTokenProvider();
        var coordinator = new ActivationCoordinator([provider], tokens);

        var outcomes = await coordinator.ActivateAsync([Request("t1", "r1")], [Account]);

        outcomes[0].Result.Should().BeOfType<ActivationResult.Activated>();
        tokens.InteractiveCalls.Should().ContainSingle();
        tokens.InteractiveCalls[0].Claims.Should().Be(ClaimsChallenge.MultiFactor);
    }

    [Fact]
    public async Task AuthenticationContextRoleRetriesWithAcrsClaims()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.InteractionRequired));
        var tokens = new FakeTokenProvider();
        var coordinator = new ActivationCoordinator([provider], tokens);
        var request = Request("t1", "r1", context: "c1");

        var outcomes = await coordinator.ActivateAsync([request], [Account]);

        outcomes[0].Result.Should().BeOfType<ActivationResult.Activated>();
        tokens.InteractiveCalls[0].Claims.Should().Be("""{"access_token":{"acrs":{"essential":true,"value":"c1"}}}""");

        provider.PushFailure(new PimException(PimErrorKind.InteractionRequired));
        provider.PushFailure(new PimException(PimErrorKind.InteractionRequired));

        var again = await coordinator.ActivateAsync([request], [Account]);

        var error = again[0].Result.Should().BeOfType<ActivationResult.Failed>().Which.Error;
        error.Kind.Should().Be(PimErrorKind.PolicyViolation);
        error.Detail.Should().Contain("\"c1\"");
    }

    [Fact]
    public async Task PendingApprovalAndPolicyViolationAreReported()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.PolicyViolation, "JustificationRule"));
        var coordinator = new ActivationCoordinator([provider], new FakeTokenProvider());

        var outcomes = await coordinator.ActivateAsync(
            [Request("t1", "r1", ""), Request("t1", "r2", "approve-me")], [Account]);

        var byKey = outcomes.ToDictionary(o => o.RoleKey, o => o.Result);
        var failed = byKey[Key("t1", "r1")].Should().BeOfType<ActivationResult.Failed>().Which.Error;
        failed.Kind.Should().Be(PimErrorKind.PolicyViolation);
        failed.Detail.Should().Be("JustificationRule");
        byKey[Key("t1", "r2")].Should().BeOfType<ActivationResult.PendingApproval>()
            .Which.Assignment.AssignmentId.Should().Be("p");
    }

    [Fact]
    public async Task ScheduledAndFailedStatusesAreMappedToResults()
    {
        var provider = new ScriptedStatusProvider(AssignmentStatus.Scheduled);
        var coordinator = new ActivationCoordinator([provider], new FakeTokenProvider());

        var scheduled = await coordinator.ActivateAsync([Request("t1", "r1")], [Account]);
        scheduled[0].Result.Should().BeOfType<ActivationResult.Scheduled>();

        var failing = new ActivationCoordinator(
            [new ScriptedStatusProvider(AssignmentStatus.Failed("nope"))], new FakeTokenProvider());

        var failed = await failing.ActivateAsync([Request("t1", "r1")], [Account]);
        failed[0].Result.Should().BeOfType<ActivationResult.Failed>().Which.Error.Detail.Should().Be("nope");
    }

    [Fact]
    public async Task UnknownIdentityOrProviderFails()
    {
        var coordinator = new ActivationCoordinator([new FakeProvider(RoleScopeKind.EntraDirectory)], new FakeTokenProvider());

        var outcomes = await coordinator.ActivateAsync(
            [
                new ActivationRequest(new RoleKey("ghost", "t", new EntraDirectoryScope("r", "/")), TimeSpan.FromMinutes(1), "j"),
                new ActivationRequest(new RoleKey("id1", "t", new GroupScope("g", GroupAccess.Member)), TimeSpan.FromMinutes(1), "j"),
            ],
            [Account]);

        outcomes.Should().AllSatisfy(o => o.Result.Should().BeOfType<ActivationResult.Failed>());
        var messages = outcomes.Select(o => ((ActivationResult.Failed)o.Result).Error.Detail).ToList();
        messages.Should().Contain("Unknown identity ghost");
        messages.Should().Contain("No provider for group");
    }

    [Fact]
    public async Task ConsentRequiredFailsWithoutInteractivePrompt()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.ConsentRequired));
        var tokens = new FakeTokenProvider();
        var coordinator = new ActivationCoordinator([provider], tokens);

        var outcomes = await coordinator.ActivateAsync([Request("t1", "r1")], [Account]);

        outcomes[0].Result.Should().BeOfType<ActivationResult.Failed>()
            .Which.Error.Kind.Should().Be(PimErrorKind.ConsentRequired);
        tokens.InteractiveCalls.Should().BeEmpty();
        provider.Activated.Should().BeEmpty();
    }

    [Fact]
    public async Task DeactivateRetriesOnInteractionRequired()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.InteractionRequired));
        var tokens = new FakeTokenProvider();
        var coordinator = new ActivationCoordinator([provider], tokens);
        var assignment = new ActiveAssignment(Key("t1", "r1"), "x", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        await coordinator.DeactivateAsync(assignment, Account);

        provider.Deactivated.Should().ContainSingle();
        tokens.InteractiveCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task CancelPendingRequestRetriesOnInteractionRequired()
    {
        var provider = new FakeProvider(RoleScopeKind.EntraDirectory);
        provider.PushFailure(new PimException(PimErrorKind.InteractionRequired));
        var tokens = new FakeTokenProvider();
        var coordinator = new ActivationCoordinator([provider], tokens);
        var assignment = new ActiveAssignment(Key("t1", "r1"), "req-9", DateTimeOffset.UtcNow, null, AssignmentStatus.PendingApproval);

        await coordinator.CancelPendingRequestAsync(assignment, Account);

        provider.Cancelled.Should().ContainSingle();
        tokens.InteractiveCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task DeactivateWithoutAProviderThrows()
    {
        var coordinator = new ActivationCoordinator([new FakeProvider(RoleScopeKind.EntraDirectory)], new FakeTokenProvider());
        var assignment = new ActiveAssignment(
            new RoleKey("id1", "t1", new GroupScope("g", GroupAccess.Member)), "x", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        (await coordinator.Invoking(c => c.DeactivateAsync(assignment, Account)).Should().ThrowAsync<PimException>())
            .Which.Status.Should().Be(501);
    }

    /// <summary>Answers every activation with one fixed status, to exercise the result mapping.</summary>
    private sealed class ScriptedStatusProvider : Elevate.Core.Providers.IPimProvider
    {
        private readonly AssignmentStatus _status;

        public ScriptedStatusProvider(AssignmentStatus status) => _status = status;

        public RoleScopeKind Kind => RoleScopeKind.EntraDirectory;

        public IReadOnlyList<string> Scopes { get; } = ["scope"];

        public Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(Identity identity, TenantContext tenant, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EligibleRole>>([]);

        public Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(Identity identity, TenantContext tenant, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActiveAssignment>>([]);

        public Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default)
            => Task.FromResult(RolePolicy.ManualDefault);

        public Task<ActiveAssignment> ActivateAsync(ActivationRequest request, Identity identity, CancellationToken ct = default)
            => Task.FromResult(new ActiveAssignment(request.RoleKey, "a", DateTimeOffset.UtcNow, null, _status));

        public Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
