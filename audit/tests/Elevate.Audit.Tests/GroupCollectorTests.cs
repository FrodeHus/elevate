using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using Elevate.Core.Networking;
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

    /// <summary>
    /// Once <c>_groupsUnavailable</c> is latched tenant-wide, the verbose cross-check would only 403
    /// again for every group, so it must not run at all.
    /// </summary>
    [Fact]
    public async Task Collect_WithVerbose_WhenGroupsAreUnavailableTenantWide_NeverRequestsTransitiveMembers()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[]}""");
        var ids = Enumerable.Range(1, 5).Select(i => $"g{i}").ToList();
        stub.On("GET", "/groups/g1?",
            """{"error":{"code":"UnknownError","message":"{\"errorCode\":\"PermissionScopeNotGranted\",\"message\":\"Authorization failed due to missing permission scope GroupMember.Read.All.\"}"}}""", 403);
        foreach (var id in ids.Skip(1))
        {
            stub.On("GET", $"/groups/{id}?", $$"""{"id":"{{id}}","displayName":"Group {{id}}","isAssignableToRole":false,"groupTypes":[]}""");
            stub.On("GET", $"/groups/{id}/members", """{"value":[]}""");
            stub.On("GET", $"assignmentScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
            stub.On("GET", $"eligibilityScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
        }
        var lines = new List<string>();

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId, lines.Add).CollectAsync(ids, CancellationToken.None);

        data.GroupsUnavailableReason.Should().Contain("GroupMember.Read.All");
        stub.RequestsMatching("transitiveMembers").Should().BeEmpty();
        lines.Should().BeEmpty();
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
    /// All 5 groups are already known from the role-assignable list route, so they enter the queue
    /// together and the first wave (of up to 4) takes g1-g4, leaving g5 for a second wave. g1's members
    /// call reveals the tenant-wide condition; g2-g4's own member reads succeed independently and are
    /// still recorded (only the offending member is dropped from the wave, per
    /// <see cref="GroupCollector"/>'s wave-merge rule) — but no second wave is ever dispatched, so g5 is
    /// never asked for at all.
    /// </summary>
    [Fact]
    public async Task Collect_WhenTheGroupScopeIsMissingOnMembers_StopsWalkingAndReportsWhy()
    {
        var stub = new StubHttpClient();
        var ids = Enumerable.Range(1, 5).Select(i => $"g{i}").ToList();
        stub.On("GET", "/groups?$filter=isAssignableToRole",
            $$"""{"value":[{{string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","displayName":"Group {{id}}","isAssignableToRole":true,"groupTypes":[]}"""))}}]}""");
        stub.On("GET", "/groups/g1/members",
            """{"error":{"code":"UnknownError","message":"{\"errorCode\":\"PermissionScopeNotGranted\",\"message\":\"Authorization failed due to missing permission scope GroupMember.Read.All.\"}"}}""", 403);
        foreach (var id in ids.Skip(1))
        {
            stub.On("GET", $"/groups/{id}/members", """{"value":[]}""");
            stub.On("GET", $"assignmentScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
            stub.On("GET", $"eligibilityScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
        }

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync([], CancellationToken.None);

        data.GroupsUnavailableReason.Should().Contain("GroupMember.Read.All");
        data.Groups.Select(g => g.Id).Should().BeEquivalentTo(["g2", "g3", "g4"], "g1 is dropped as the offending member, and g5's wave is never dispatched");
        data.UnreadableGroups.Should().Be(0);
        stub.RequestsMatching("/groups/g5").Should().BeEmpty("the walk stops before a second wave is ever dispatched");
    }

    /// <summary>
    /// Same rationale as <see cref="Collect_WhenTheGroupScopeIsMissingOnMembers_StopsWalkingAndReportsWhy"/>,
    /// but for the metadata read: here the groups must NOT be pre-known from the list route (which would
    /// skip the metadata call entirely), so they arrive as 5 seeds and each needs its own network fetch.
    /// </summary>
    [Fact]
    public async Task Collect_WhenTheGroupScopeIsMissing_StopsWalkingAndReportsWhy()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[]}""");
        var ids = Enumerable.Range(1, 5).Select(i => $"g{i}").ToList();
        stub.On("GET", "/groups/g1?",
            """{"error":{"code":"UnknownError","message":"{\"errorCode\":\"PermissionScopeNotGranted\",\"message\":\"Authorization failed due to missing permission scope GroupMember.Read.All.\"}"}}""", 403);
        foreach (var id in ids.Skip(1))
        {
            stub.On("GET", $"/groups/{id}?", $$"""{"id":"{{id}}","displayName":"Group {{id}}","isAssignableToRole":false,"groupTypes":[]}""");
            stub.On("GET", $"/groups/{id}/members", """{"value":[]}""");
            stub.On("GET", $"assignmentScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
            stub.On("GET", $"eligibilityScheduleInstances?$filter=groupId eq '{id}'", """{"value":[]}""");
        }

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(ids, CancellationToken.None);

        data.GroupsUnavailableReason.Should().Contain("GroupMember.Read.All");
        data.Groups.Select(g => g.Id).Should().BeEquivalentTo(["g2", "g3", "g4"], "g1 is dropped as the offending member, and g5's wave is never dispatched");
        data.UnreadableGroups.Should().Be(0, "a tenant-wide refusal is reported once, not counted per group");
        stub.RequestsMatching("/groups/g5").Should().BeEmpty("the walk stops before a second wave is ever dispatched");
    }

    /// <summary>
    /// g1's members respond asynchronously (after a short delay), so it is guaranteed to complete after
    /// its wave-siblings even though it was dispatched first — proving the collector doesn't depend on
    /// completion order matching dispatch order.
    /// </summary>
    [Fact]
    public async Task Collect_WithSixGroups_CollectsAllOfThemRegardlessOfWaveOrder()
    {
        var stub = new StubHttpClient();
        var ids = Enumerable.Range(1, 6).Select(i => $"g{i}").ToList();
        stub.On("GET", "/groups?$filter=isAssignableToRole",
            $$"""{"value":[{{string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","displayName":"Group {{id}}","isAssignableToRole":true,"groupTypes":[]}"""))}}]}""");
        stub.On("GET", "/groups/g1/members", async _ =>
        {
            await Task.Delay(50);
            return new HttpResponseData(200, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes("""{"value":[]}"""));
        });
        foreach (var id in ids.Skip(1))
        {
            stub.On("GET", $"/groups/{id}/members", """{"value":[]}""");
        }

        foreach (var id in ids)
        {
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
