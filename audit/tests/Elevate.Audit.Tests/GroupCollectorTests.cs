using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class GroupCollectorTests
{
    private static StubHttpClient Tenant()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """
            {"value":[{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[]}]}
            """);
        stub.On("GET", "/groups/g1/members", """
            {"value":[
              {"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Sam Chen","userPrincipalName":"sam.chen@contoso.com","userType":"Member","accountEnabled":true},
              {"@odata.type":"#microsoft.graph.group","id":"g2","displayName":"Platform Team"},
              {"@odata.type":"#microsoft.graph.device","id":"d1","displayName":"LAPTOP-1"}
            ]}
            """);
        stub.On("GET", "/groups/g2?", """{"id":"g2","displayName":"Platform Team","isAssignableToRole":false,"groupTypes":["DynamicMembership"]}""");
        stub.On("GET", "/groups/g2/members", """
            {"value":[
              {"@odata.type":"#microsoft.graph.group","id":"g1","displayName":"Tier 0 Admins"},
              {"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1","displayName":"Deploy Bot","servicePrincipalType":"Application"}
            ]}
            """);
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g1'", """
            {"value":[{"id":"gp1","principalId":"u1","groupId":"g1","accessId":"member","assignmentType":"Assigned","startDateTime":"2025-01-01T00:00:00Z","endDateTime":null}]}
            """);
        stub.On("GET", "eligibilityScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g2'", """{"error":{"code":"Forbidden","message":"not onboarded"}}""", 403);
        return stub;
    }

    [Fact]
    public async Task Collect_WalksNestedGroups_StopsOnCycles_AndReadsPim()
    {
        var stub = Tenant();

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1"], CancellationToken.None);

        data.Groups.Select(g => g.Id).Should().BeEquivalentTo(["g1", "g2"]);
        var g1 = data.Groups.Single(g => g.Id == "g1");
        g1.DirectMembers.Select(m => (m.Id, m.Type)).Should().BeEquivalentTo([("u1", PrincipalType.User), ("g2", PrincipalType.Group), ("d1", PrincipalType.Device)]);
        g1.PimStatus.Should().Be(PimStatus.Onboarded);
        g1.PimAssignments.Should().ContainSingle().Which.IsPermanent.Should().BeTrue();
        var g2 = data.Groups.Single(g => g.Id == "g2");
        g2.IsDynamic.Should().BeTrue();
        g2.IsAssignableToRole.Should().BeFalse();
        g2.PimStatus.Should().Be(PimStatus.NotOnboarded);
        data.Principals.Select(p => p.Id).Should().BeEquivalentTo(["u1", "sp1"], "devices are counted, not resolved");
        data.PimUnavailableReason.Should().BeNull();
        stub.RequestsMatching("/groups/g1/members").Should().HaveCount(1, "each group is visited once even when nested in a cycle");
    }

    [Fact]
    public async Task Collect_WhenThePimScopeIsMissing_StopsAskingAndReportsWhy()
    {
        var stub = Tenant();
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g1'",
            """{"error":{"code":"UnknownError","message":"{\"errorCode\":\"PermissionScopeNotGranted\",\"message\":\"Authorization failed due to missing permission scope PrivilegedAssignmentSchedule.Read.AzureADGroup.\"}"}}""", 403);

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync([], CancellationToken.None);

        data.PimUnavailableReason.Should().Contain("PrivilegedAssignmentSchedule.Read.AzureADGroup");
        data.Groups.Should().OnlyContain(g => g.PimStatus == PimStatus.Unknown);
        stub.RequestsMatching("privilegedAccess/group").Should().HaveCount(1);
    }
}
