using Elevate.Cli.Selection;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class SessionTests
{
    [Fact]
    public async Task RefreshLoadsRolesAndAssignments()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        t.Entra.Eligible.Add(TestSession.Role("r2", "User Administrator"));
        var now = DateTimeOffset.UtcNow;
        t.Entra.Assignments.Add(new ActiveAssignment(TestSession.EntraKey("r1"), "a", now, now.AddHours(1), AssignmentStatus.Active));

        await t.Session.RefreshAllAsync();

        t.Session.Roles[TestSession.Tenant.Key].Select(r => r.DisplayName).Should().Equal("Global Reader", "User Administrator");
        t.Session.Active.Should().ContainKey(TestSession.EntraKey("r1"));
        t.Session.LoadedTenants.Should().Contain(TestSession.Tenant.Key);
        t.Session.TenantErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsentRefusalSwitchesTenantToManualRoles()
    {
        using var t = new TestSession();
        t.Entra.EligibleError = new PimException(PimErrorKind.ConsentRequired);

        await t.Session.RefreshAllAsync();

        var tenant = t.Session.Tenant(TestSession.Tenant.Key)!;
        tenant.DiscoveryMode.Should().Be(DiscoveryMode.ManualRoles);
        new AppStateStore(t.Directory).Load().Tenants.Single().DiscoveryMode.Should().Be(DiscoveryMode.ManualRoles, "the latch is persisted");
    }

    [Fact]
    public async Task ManualRolesShowWhenDiscoveryIsOff()
    {
        using var t = new TestSession();
        t.Session.SetManualRoles(TestSession.Tenant.Key, [new Core.Catalogue.ManualRole(TestSession.Tenant.Key, new EntraDirectoryScope("r9", "/"), "Helpdesk Administrator")]);

        await t.Session.RefreshAllAsync();

        t.Session.Roles[TestSession.Tenant.Key].Should().ContainSingle(r => r.DisplayName == "Helpdesk Administrator" && r.Source == RoleSource.Manual);
    }

    [Fact]
    public async Task ActivationRemembersReasonAndDuration()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();

        var outcomes = await t.Session.ActivateAsync([new ActivationRequest(TestSession.EntraKey("r1"), TimeSpan.FromHours(2), "ticket 42")], deactivateFirst: false);

        outcomes.Should().ContainSingle().Which.Result.Should().BeOfType<ActivationResult.Activated>();
        t.Session.Active.Should().ContainKey(TestSession.EntraKey("r1"));
        var memory = new AppStateStore(t.Directory).Load().MemoryFor(TestSession.EntraKey("r1"));
        memory.Should().NotBeNull();
        memory!.Justification.Should().Be("ticket 42");
        memory.LastDuration.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task ExtendDeactivatesFirst()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        var now = DateTimeOffset.UtcNow;
        t.Entra.Assignments.Add(new ActiveAssignment(TestSession.EntraKey("r1"), "a", now, now.AddMinutes(5), AssignmentStatus.Active));
        await t.Session.RefreshAllAsync();

        var outcomes = await t.Session.ActivateAsync([new ActivationRequest(TestSession.EntraKey("r1"), TimeSpan.FromHours(1), "again")], deactivateFirst: true);

        t.Entra.Deactivated.Should().ContainSingle();
        t.Entra.Activated.Should().ContainSingle();
        outcomes.Single().Result.Should().BeOfType<ActivationResult.Activated>();
    }

    [Fact]
    public async Task FailedReactivationAfterDeactivationIsReportedAsSuch()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        var now = DateTimeOffset.UtcNow;
        t.Entra.Assignments.Add(new ActiveAssignment(TestSession.EntraKey("r1"), "a", now, now.AddMinutes(5), AssignmentStatus.Active));
        await t.Session.RefreshAllAsync();
        t.Entra.Failures.Enqueue(new PimException(PimErrorKind.Network, "boom"));

        // The queued failure is consumed by the deactivation; queue a second for the activation.
        t.Entra.Failures.Enqueue(new PimException(PimErrorKind.Network, "boom"));
        var outcomes = await t.Session.ActivateAsync([new ActivationRequest(TestSession.EntraKey("r1"), TimeSpan.FromHours(1), "again")], deactivateFirst: true);

        outcomes.Single().Result.Should().BeOfType<ActivationResult.Failed>()
            .Which.Error.UserMessage.Should().Contain("deactivate");
    }

    [Fact]
    public async Task ProfilePlanSkipsActiveAndRunsTheRest()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        t.Entra.Eligible.Add(TestSession.Role("r2", "User Administrator"));
        var now = DateTimeOffset.UtcNow;
        t.Entra.Assignments.Add(new ActiveAssignment(TestSession.EntraKey("r1"), "a", now, now.AddHours(1), AssignmentStatus.Active));
        await t.Session.RefreshAllAsync();
        var profile = t.Session.SaveProfile("Morning", [TestSession.EntraKey("r1"), TestSession.EntraKey("r2"), TestSession.EntraKey("r3")]);

        var plan = t.Session.Plan(profile);
        plan.Select(i => i.Disposition).Should().Equal(ProfilePlanDisposition.AlreadyActive, ProfilePlanDisposition.Activate, ProfilePlanDisposition.NotEligible);

        var outcomes = await t.Session.RunProfileAsync(profile, plan, "standup", null, null, null, CancellationToken.None);

        outcomes.Should().ContainSingle().Which.RoleKey.Should().Be(TestSession.EntraKey("r2"));
        var saved = new AppStateStore(t.Directory).Load().Profiles.Single();
        saved.LastJustification.Should().Be("standup");
        saved.Entries.First(e => e.RoleKey == TestSession.EntraKey("r2")).LastDuration.Should().Be(RolePolicy.ManualDefault.DefaultDuration);
    }

    [Fact]
    public void ProfilesResolveByNamePrefixAndImport()
    {
        using var t = new TestSession();
        t.Session.SaveProfile("Morning", [TestSession.EntraKey("r1")]);
        t.Session.SaveProfile("Maintenance", [TestSession.EntraKey("r2")]);

        t.Session.FindProfile("morning")!.Name.Should().Be("Morning");
        t.Session.FindProfile("Ma")!.Name.Should().Be("Maintenance");
        t.Session.FindProfile("M").Should().BeNull("two profiles match the prefix");

        var other = new AppState();
        other.UpsertProfile(new ActivationProfile("Morning", [new ActivationProfile.Entry(TestSession.EntraKey("r5"))]));
        other.UpsertProfile(new ActivationProfile("Evening", [new ActivationProfile.Entry(TestSession.EntraKey("r6"))]));
        var imported = t.Session.ImportProfiles(other);
        imported.Select(p => p.Name).Should().BeEquivalentTo(["Evening"], "a same-named profile is kept, not replaced");
        t.Session.Profiles.Should().HaveCount(3);
    }

    [Fact]
    public async Task ApprovalsAreReadAndDecided()
    {
        using var t = new TestSession();
        t.EntraApprovals.Pending.Add(new ApprovalRequest("req1", TestSession.Tenant.Key, RoleScopeKind.EntraDirectory, ApprovalAction.Activate, "Global Reader", "Sam"));
        await t.Session.RefreshAllAsync();

        var request = t.Session.ApprovalsOrdered.Should().ContainSingle().Subject;
        await t.Session.DecideAsync(request, approve: true, "ok");

        t.EntraApprovals.Decisions.Should().ContainSingle().Which.Should().Be(("req1", true, "ok"));
        t.Session.ApprovalsOrdered.Should().BeEmpty();
        t.Settings.LastApprovalJustification.Should().Be("ok");
    }

    [Fact]
    public async Task RoleSelectorResolvesIdsNamesAndAmbiguity()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        t.Entra.Eligible.Add(TestSession.Role("r2", "Reader of Things"));
        t.Entra.Eligible.Add(TestSession.Role("r3", "User Administrator"));
        await t.Session.RefreshAllAsync();

        RoleSelector.Resolve(t.Session, ["global reader"], new RoleFilter()).Should().ContainSingle().Which.Key.Should().Be(TestSession.EntraKey("r1"));
        RoleSelector.Resolve(t.Session, [ShortId.For(TestSession.EntraKey("r3"))], new RoleFilter()).Should().ContainSingle().Which.DisplayName.Should().Be("User Administrator");
        RoleSelector.Resolve(t.Session, ["user admin", "reader of"], new RoleFilter()).Should().HaveCount(2);

        var ambiguous = () => RoleSelector.Resolve(t.Session, ["reader"], new RoleFilter());
        ambiguous.Should().Throw<Cli.Infrastructure.CliException>().Which.Message.Should().Contain("matches 2 roles");

        var missing = () => RoleSelector.Resolve(t.Session, ["owner"], new RoleFilter());
        missing.Should().Throw<Cli.Infrastructure.CliException>().Which.ExitCode.Should().Be(Cli.Infrastructure.ExitCodes.NotFound);

        RoleSelector.Filtered(t.Session, new RoleFilter(Tenant: "contoso")).Should().HaveCount(3);
        RoleSelector.Filtered(t.Session, new RoleFilter(Tenant: "fabrikam")).Should().BeEmpty();
        RoleSelector.Filtered(t.Session, new RoleFilter(Kind: RoleScopeKind.AzureResource)).Should().BeEmpty();
    }

    [Fact]
    public async Task SignOutForgetsEverythingOfTheAccount()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();
        t.Session.SaveProfile("P", [TestSession.EntraKey("r1")]);

        await t.Session.SignOutAsync(TestSession.Account);

        t.Session.Identities.Should().BeEmpty();
        t.Session.Tenants.Should().BeEmpty();
        t.Session.Roles.Should().BeEmpty();
        t.Session.Profiles.Single().Entries.Should().BeEmpty();
    }

    [Fact]
    public void CorruptStateIsQuarantined()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), "{broken");
        var session = new Session.ElevateSession(new AppStateStore(dir), new Cli.Infrastructure.CliSettings(dir), new FakeTokenProvider(), new NoHttpClient(), [], []);

        session.Load();

        session.LoadNotice.Should().Contain("state.json.bak");
        File.Exists(Path.Combine(dir, "state.json.bak")).Should().BeTrue();
        session.Identities.Should().BeEmpty();
        Directory.Delete(dir, recursive: true);
    }
}
