using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ActivationCollectorTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-03-17T12:00:00Z");

    private static Task<IReadOnlyList<ActivationRecord>> CollectAsync(StubHttpClient stub) =>
        new ActivationCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(Since, CancellationToken.None);

    [Fact]
    public async Task Collect_MapsARoleActivation_ToThePrincipalAndEveryRoleKey()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/auditLogs/directoryAudits", """
            {"value":[{
              "id":"a1","activityDateTime":"2026-04-20T08:00:00Z","activityDisplayName":"Add member to role completed (PIM activation)",
              "category":"RoleManagement","result":"success","loggedByService":"PIM",
              "initiatedBy":{"user":{"id":"u1"}},
              "targetResources":[
                {"id":"u1","type":"User","userPrincipalName":"sam.chen@contoso.com"},
                {"id":"rd-ga","type":"Role","displayName":"Global Administrator"}]}]}
            """);

        var records = await CollectAsync(stub);

        var record = records.Should().ContainSingle().Subject;
        record.Should().BeEquivalentTo(new { PrincipalId = "u1", System = RoleSystem.Entra, Scope = (string?)null });
        record.ActivatedAt.Should().Be(DateTimeOffset.Parse("2026-04-20T08:00:00Z"));
        record.RoleKeys.Should().Equal("rd-ga", "Global Administrator");
        stub.RequestsMatching("activityDateTime ge 2026-03-17T12:00:00Z").Should().ContainSingle("the lookback goes in the filter");
    }

    [Fact]
    public async Task Collect_MapsAGroupActivation_ToTheGroupAsBothRoleAndScope()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/auditLogs/directoryAudits", """
            {"value":[{
              "id":"a1","activityDateTime":"2026-09-01T08:30:00Z","activityDisplayName":"Add member to group completed (PIM activation)",
              "category":"GroupManagement","result":"success","loggedByService":"PIM",
              "targetResources":[
                {"id":"u1","type":"User"},
                {"id":"g1","type":"Group","displayName":"Tier 0 Admins"}]}]}
            """);

        var records = await CollectAsync(stub);

        records.Should().ContainSingle().Which.Should().BeEquivalentTo(new { PrincipalId = "u1", System = RoleSystem.Group, Scope = "g1" });
    }

    [Fact]
    public async Task Collect_IgnoresEverythingThatIsNotASuccessfulActivation()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/auditLogs/directoryAudits", """
            {"value":[
              {"id":"a1","activityDateTime":"2026-04-20T08:00:00Z","activityDisplayName":"Remove member from role (PIM deactivate)","result":"success","targetResources":[{"id":"u1","type":"User"},{"id":"rd-ga","type":"Role"}]},
              {"id":"a2","activityDateTime":"2026-04-20T08:00:00Z","activityDisplayName":"Add member to role completed (PIM activation)","result":"failure","targetResources":[{"id":"u1","type":"User"},{"id":"rd-ga","type":"Role"}]},
              {"id":"a3","activityDateTime":"2026-04-20T08:00:00Z","activityDisplayName":"Add member to role requested (PIM activation)","result":"success","targetResources":[{"id":"u1","type":"User"},{"id":"rd-ga","type":"Role"}]},
              {"id":"a4","activityDateTime":"2026-04-20T08:00:00Z","activityDisplayName":"Add eligible member to role (PIM activation request denied)","result":"success","targetResources":[{"id":"u1","type":"User"},{"id":"rd-ga","type":"Role"}]},
              {"id":"a5","activityDateTime":"2026-04-20T08:00:00Z","activityDisplayName":"Add member to role completed (PIM activation)","result":"success","targetResources":[{"id":"u1","type":"User"}]}
            ]}
            """);

        var records = await CollectAsync(stub);

        records.Should().BeEmpty("a deactivation, a failure, a request, a denial and an entry naming no role are all not use of an eligibility");
    }

    [Fact]
    public void Azure_Activation_TakesSelfActivationsAndExtensions_AndSkipsAdminAssignmentsAndOldOnes()
    {
        static AzureCollector.WireRequest Request(string type, string createdOn, string status = "Provisioned") => new(
            "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/r1",
            "r1",
            new AzureCollector.RequestProperties("/subscriptions/sub1", SnapshotBuilder.AzureRolePrefix + "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "u1", type, status, DateTimeOffset.Parse(createdOn)));

        AzureCollector.Activation(Request("SelfActivate", "2026-04-20T08:00:00Z"), Since).Should().BeEquivalentTo(
            new { PrincipalId = "u1", System = RoleSystem.Azure, Scope = "/subscriptions/sub1" });
        AzureCollector.Activation(Request("SelfActivate", "2026-04-20T08:00:00Z"), Since)!.RoleKeys.Should().Equal("8e3af657-a8ff-443c-a75c-2fe8c4bcb635");
        AzureCollector.Activation(Request("SelfExtend", "2026-04-20T08:00:00Z"), Since).Should().NotBeNull();
        AzureCollector.Activation(Request("AdminAssign", "2026-04-20T08:00:00Z"), Since).Should().BeNull("an administrator's assignment is not use of an eligibility");
        AzureCollector.Activation(Request("SelfActivate", "2026-04-20T08:00:00Z", "Revoked"), Since).Should().BeNull();
        AzureCollector.Activation(Request("SelfActivate", "2026-01-01T08:00:00Z"), Since).Should().BeNull("it predates the window");
    }
}
