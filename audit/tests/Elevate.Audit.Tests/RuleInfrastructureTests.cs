using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class RuleInfrastructureTests
{
    [Fact]
    public void Expand_ReturnsUsersWithTheirPath_TerminatesOnCycles_AndSkipsDevices()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .ServicePrincipal("sp1", "Deploy Bot")
            .Group("g1", "Tier 0 Admins", members: [("u1", PrincipalType.User), ("g2", PrincipalType.Group), ("d1", PrincipalType.Device)])
            .Group("g2", "Platform Team", assignable: false, members: [("u2", PrincipalType.User), ("sp1", PrincipalType.ServicePrincipal), ("g1", PrincipalType.Group)])
            .Build();
        var context = new RuleContext(snapshot, new AuditOptions());

        var members = context.Expansion.Expand("g1");

        members.Select(m => m.PrincipalId).Should().Equal("u1", "u2", "sp1");
        members.Single(m => m.PrincipalId == "u1").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins");
        members.Single(m => m.PrincipalId == "u2").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins", "Platform Team");
        context.Expansion.NestedGroups("g1").Select(g => g.Id).Should().BeEquivalentTo(["g1", "g2"]);
    }

    [Fact]
    public void Expand_UnknownGroup_IsEmpty()
    {
        var context = new RuleContext(SnapshotBuilder.Contoso().Build(), new AuditOptions());

        context.Expansion.Expand("missing").Should().BeEmpty();
    }

    [Fact]
    public void Privilege_UsesTheEntraFlag_AndTheAzureList()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .AzureRole("11111111-0000-0000-0000-000000000001", "Custom Deployer", "CustomRole", "Microsoft.Compute/*", "Microsoft.Authorization/roleAssignments/write")
            .AzureRole("11111111-0000-0000-0000-000000000002", "Custom Viewer", "CustomRole", "*/read")
            .Build();
        var context = new RuleContext(snapshot, new AuditOptions());

        context.IsPrivilegedEntra("rd-ga").Should().BeTrue();
        context.IsPrivilegedEntra("rd-reader").Should().BeFalse();
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "8e3af657-a8ff-443c-a75c-2fe8c4bcb635").Should().Be(Severity.High);
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "b24988ac-6180-42a0-ab88-20f7382dd24c").Should().Be(Severity.Medium);
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "acdd72a7-3385-48ef-bd42-f606fba81ae7").Should().BeNull();
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "11111111-0000-0000-0000-000000000001").Should().Be(Severity.Medium, "a custom role that can write role assignments is privileged");
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "11111111-0000-0000-0000-000000000002").Should().BeNull();
    }

    [Fact]
    public void Privilege_WildcardAuthorizationActions_AreAlsoPrivileged()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .AzureRole("11111111-0000-0000-0000-000000000003", "Custom Authorization Wildcard", "CustomRole", "Microsoft.Authorization/*")
            .AzureRole("11111111-0000-0000-0000-000000000004", "Custom Compute Wildcard", "CustomRole", "Microsoft.Compute/*")
            .Build();
        var context = new RuleContext(snapshot, new AuditOptions());

        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "11111111-0000-0000-0000-000000000003").Should().Be(Severity.Medium);
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "11111111-0000-0000-0000-000000000004").Should().BeNull();
    }

    [Fact]
    public void AllRoles_MakesEverythingPrivileged()
    {
        var context = new RuleContext(SnapshotBuilder.Contoso().Build(), new AuditOptions(AllRoles: true));

        context.IsPrivilegedEntra("rd-reader").Should().BeTrue();
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "acdd72a7-3385-48ef-bd42-f606fba81ae7").Should().Be(Severity.Medium);
    }

    [Fact]
    public void AzureScope_TenantRoot_IsLabeledAndKindManagementGroup()
    {
        var context = new RuleContext(SnapshotBuilder.Contoso().Build(), new AuditOptions());

        var scope = context.AzureScope("/");

        scope.DisplayName.Should().Be("Tenant root");
        scope.Kind.Should().Be(ScopeKind.ManagementGroup);
    }

    [Fact]
    public void Principal_FallsBackToGroupsAndThenToUnknown()
    {
        var context = new RuleContext(SnapshotBuilder.Contoso().Group("g1", "Tier 0 Admins").Build(), new AuditOptions());

        context.Principal("g1").Should().BeEquivalentTo(new { DisplayName = "Tier 0 Admins", Type = PrincipalType.Group });
        context.Principal("nobody").Should().BeEquivalentTo(new { Id = "nobody", DisplayName = "<unknown principal nobody>", Type = PrincipalType.Unknown });
    }

    [Fact]
    public void Runner_SortsBySeverity_HonoursIgnore_AndFiltersVisibility()
    {
        var findings = RuleRunner.Run(SnapshotBuilder.Contoso().Build(), new AuditOptions(Ignored: ["ga-count"]), [new FakeRule("X-LOW", Severity.Low), new FakeRule("GA-COUNT", Severity.High), new FakeRule("Y-HIGH", Severity.High)]);

        findings.Select(f => f.Id).Should().Equal("Y-HIGH", "X-LOW");
        RuleRunner.HasHigh(findings).Should().BeTrue();
        RuleRunner.Visible(findings, new AuditOptions(MinSeverity: Severity.Medium)).Select(f => f.Id).Should().Equal("Y-HIGH");
        RuleRunner.All.Select(r => r.Code).Should().HaveCount(10).And.OnlyHaveUniqueItems();
    }

    private sealed class FakeRule(string code, Severity severity) : IRule
    {
        public string Code => code;

        public IEnumerable<Finding> Evaluate(RuleContext context)
        {
            yield return new Finding(code, severity,
                new FindingPrincipal("p", "P", null, PrincipalType.User, false, true),
                new FindingRole("r", "R", null, true, RoleSystem.Entra),
                new FindingScope("/", "Directory", ScopeKind.Directory), [], "fix", "https://example.invalid",
                new FindingEvidence("a", null, null, null, null));
        }
    }
}
