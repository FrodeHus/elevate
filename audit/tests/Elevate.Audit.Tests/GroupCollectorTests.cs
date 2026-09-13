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
        var members = Uri.UnescapeDataString(stub.RequestsMatching("/groups/g1/members")[0].Url.AbsoluteUri);
        members.Should().StartWith("https://graph.microsoft.com/beta/groups/g1/members", "v1.0 has a documented known issue that omits service principals from group members");
        members.Should().Contain("$select=id,displayName,userPrincipalName,userType,accountEnabled,servicePrincipalType", "userType is not in the default user projection, so guests would read as members");
        data.Principals.Single(p => p.Id == "u1").AccountEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Collect_RoundTripsSecurityEnabledMailEnabledAndVisibility()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[]}""");
        stub.On("GET", "/groups/g1?", """{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[],"securityEnabled":false,"mailEnabled":true,"visibility":"Private"}""");
        stub.On("GET", "/groups/g1/members", """{"value":[]}""");
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");
        stub.On("GET", "eligibilityScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1"], CancellationToken.None);

        var g1 = data.Groups.Should().ContainSingle().Which;
        g1.SecurityEnabled.Should().BeFalse();
        g1.MailEnabled.Should().BeTrue();
        g1.Visibility.Should().Be("Private");
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

    [Fact]
    public async Task Collect_WhenANestedGroupCannotBeRead_SkipsItAndKeepsWalking()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[]}""");
        stub.On("GET", "/groups/g1/members", """
            {"value":[
              {"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Sam Chen","userPrincipalName":"sam.chen@contoso.com","userType":"Member","accountEnabled":true},
              {"@odata.type":"#microsoft.graph.group","id":"g9","displayName":"Restricted Group"}
            ]}
            """);
        stub.On("GET", "/groups/g1?", """{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[]}""");
        stub.On("GET", "/groups/g9?", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""", 403);
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");
        stub.On("GET", "eligibilityScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1"], CancellationToken.None);

        data.Groups.Select(g => g.Id).Should().BeEquivalentTo(["g1"], "the unreadable nested group is skipped, not collected");
        var g1 = data.Groups.Single(g => g.Id == "g1");
        g1.DirectMembers.Select(m => (m.Id, m.Type)).Should().Contain(("g9", PrincipalType.Group), "the parent still lists it as a member");
        data.UnreadableGroups.Should().Be(1, "the skip is counted so the report can say the expansion is incomplete");
        data.GroupsUnavailableReason.Should().BeNull("one refused group is not a tenant-wide condition");
    }

    [Fact]
    public async Task Collect_WithVerbose_WhenTransitiveMembersDisagreesWithTheWalk_LogsTheMismatch()
    {
        var stub = Tenant();
        stub.On("GET", "/groups/g1/transitiveMembers", """
            {"value":[
              {"@odata.type":"#microsoft.graph.user","id":"u1"},
              {"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1"},
              {"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp-extra"}
            ]}
            """);
        stub.On("GET", "/groups/g2/transitiveMembers", """{"value":[{"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1"}]}""");
        var lines = new List<string>();

        await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId, lines.Add).CollectAsync(["g1"], CancellationToken.None);

        lines.Should().ContainSingle().Which.Should().Be("Tier 0 Admins: walk found 2 members, transitiveMembers reports 3");
    }

    [Fact]
    public async Task Collect_WithVerbose_WhenTransitiveMembersAgrees_LogsNothing()
    {
        var stub = Tenant();
        stub.On("GET", "/groups/g1/transitiveMembers", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1"},{"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1"}]}""");
        stub.On("GET", "/groups/g2/transitiveMembers", """{"value":[{"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1"}]}""");
        var lines = new List<string>();

        await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId, lines.Add).CollectAsync(["g1"], CancellationToken.None);

        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Collect_WithoutVerbose_NeverRequestsTransitiveMembers()
    {
        var stub = Tenant();

        await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1"], CancellationToken.None);

        stub.RequestsMatching("transitiveMembers").Should().BeEmpty();
    }

    [Fact]
    public async Task Collect_WhenAGroupsMembersAreForbidden_KeepsTheGroupWithNoMembers()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[]}""");
        stub.On("GET", "/groups/g1?", """{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[]}""");
        stub.On("GET", "/groups/g1/members", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""", 403);
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");
        stub.On("GET", "eligibilityScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1"], CancellationToken.None);

        var g1 = data.Groups.Should().ContainSingle().Which;
        g1.Id.Should().Be("g1");
        g1.DirectMembers.Should().BeEmpty();
        data.UnreadableGroups.Should().Be(1);
        data.GroupsUnavailableReason.Should().BeNull();
    }

    /// <summary>
    /// g1 and g2 are both top-level seeds, so bounded concurrency (<see cref="GroupCollector"/> fetches
    /// up to 4 groups per wave) dispatches them together: g2's own calls may still go out even though g1
    /// reveals the tenant-wide condition. What must hold is that nothing from that wave is trusted or
    /// recorded once any member of it hits the missing-scope 403 — g2 is stubbed to succeed on its own
    /// merits, and is still discarded.
    /// </summary>
    [Fact]
    public async Task Collect_WhenTheGroupScopeIsMissingOnMembers_StopsWalkingAndReportsWhy()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[]}""");
        stub.On("GET", "/groups/g1?", """{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[]}""");
        stub.On("GET", "/groups/g1/members",
            """{"error":{"code":"UnknownError","message":"{\"errorCode\":\"PermissionScopeNotGranted\",\"message\":\"Authorization failed due to missing permission scope GroupMember.Read.All.\"}"}}""", 403);
        stub.On("GET", "/groups/g2?", """{"id":"g2","displayName":"Platform Team","isAssignableToRole":false,"groupTypes":[]}""");
        stub.On("GET", "/groups/g2/members", """{"value":[]}""");
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g2'", """{"value":[]}""");
        stub.On("GET", "eligibilityScheduleInstances?$filter=groupId eq 'g2'", """{"value":[]}""");

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1", "g2"], CancellationToken.None);

        data.GroupsUnavailableReason.Should().Contain("GroupMember.Read.All");
        data.Groups.Should().BeEmpty("the whole wave is discarded once any group in it reveals the tenant-wide condition, even a sibling that otherwise succeeded");
        data.UnreadableGroups.Should().Be(0);
    }

    /// <summary>Same rationale as <see cref="Collect_WhenTheGroupScopeIsMissingOnMembers_StopsWalkingAndReportsWhy"/>, for the metadata read.</summary>
    [Fact]
    public async Task Collect_WhenTheGroupScopeIsMissing_StopsWalkingAndReportsWhy()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[]}""");
        stub.On("GET", "/groups/g1?",
            """{"error":{"code":"UnknownError","message":"{\"errorCode\":\"PermissionScopeNotGranted\",\"message\":\"Authorization failed due to missing permission scope GroupMember.Read.All.\"}"}}""", 403);
        stub.On("GET", "/groups/g2?", """{"id":"g2","displayName":"Platform Team","isAssignableToRole":false,"groupTypes":[]}""");
        stub.On("GET", "/groups/g2/members", """{"value":[]}""");
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g2'", """{"value":[]}""");
        stub.On("GET", "eligibilityScheduleInstances?$filter=groupId eq 'g2'", """{"value":[]}""");

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1", "g2"], CancellationToken.None);

        data.GroupsUnavailableReason.Should().Contain("GroupMember.Read.All");
        data.Groups.Should().BeEmpty("the whole wave is discarded once any group in it reveals the tenant-wide condition, even a sibling that otherwise succeeded");
        data.UnreadableGroups.Should().Be(0, "a tenant-wide refusal is reported once, not counted per group");
    }

    [Fact]
    public async Task Collect_WithSixGroups_CollectsAllOfThemRegardlessOfWaveOrder()
    {
        var stub = new StubHttpClient();
        var ids = Enumerable.Range(1, 6).Select(i => $"g{i}").ToList();
        stub.On("GET", "/groups?$filter=isAssignableToRole",
            $$"""{"value":[{{string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","displayName":"Group {{id}}","isAssignableToRole":true,"groupTypes":[]}"""))}}]}""");
        foreach (var id in ids)
        {
            stub.On("GET", $"/groups/{id}/members", """{"value":[]}""");
            stub.On("GET", $"assignmentScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
            stub.On("GET", $"eligibilityScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
        }

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync([], CancellationToken.None);

        data.Groups.Select(g => g.Id).Should().BeEquivalentTo(ids);
        data.Groups.Select(g => g.Id).Should().BeInAscendingOrder(StringComparer.Ordinal, "output is sorted for determinism regardless of how the waves complete");
    }

    [Fact]
    public async Task Collect_With30Groups_EmitsAProgressNoteAfterTheFirst25()
    {
        var stub = new StubHttpClient();
        var ids = Enumerable.Range(1, 30).Select(i => $"g{i}").ToList();
        stub.On("GET", "/groups?$filter=isAssignableToRole",
            $$"""{"value":[{{string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","displayName":"Group {{id}}","isAssignableToRole":true,"groupTypes":[]}"""))}}]}""");
        foreach (var id in ids)
        {
            stub.On("GET", $"/groups/{id}/members", """{"value":[]}""");
            stub.On("GET", $"assignmentScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
            stub.On("GET", $"eligibilityScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
        }

        var notes = new List<string>();
        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId, progress: notes.Add).CollectAsync([], CancellationToken.None);

        data.Groups.Should().HaveCount(30);
        notes.Should().Contain(n => n.Contains("Expanded", StringComparison.Ordinal) && n.Contains("groups", StringComparison.Ordinal));
    }
}
