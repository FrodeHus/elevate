using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class DirectoryRoleCollectorTests
{
    private const string GlobalAdminTemplate = "62e90394-69f5-4237-9190-012177145e10";

    [Fact]
    public async Task Collect_FollowsPaging_MapsInstances_AndFallsBackToTheCatalogue()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/roleManagement/directory/roleDefinitions", $$"""
            {"value":[
              {"id":"rd-ga","templateId":"{{GlobalAdminTemplate}}","displayName":"Global Administrator","isBuiltIn":true},
              {"id":"rd-reader","templateId":"f2ef992c-3afb-46b9-b7cf-a126ee74c451","displayName":"Global Reader","isPrivileged":false,"isBuiltIn":true}
            ]}
            """);
        stub.On("GET", "roleAssignmentScheduleInstances?$skiptoken=page2", """
            {"value":[
              {"id":"a2","principalId":"g1","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Assigned","memberType":"Direct",
               "startDateTime":"2025-01-01T00:00:00Z","endDateTime":null,
               "principal":{"@odata.type":"#microsoft.graph.group","id":"g1","displayName":"Tier 0 Admins"}}
            ]}
            """);
        stub.On("GET", "roleAssignmentScheduleInstances?$expand", """
            {"@odata.nextLink":"https://graph.microsoft.com/v1.0/roleManagement/directory/roleAssignmentScheduleInstances?$skiptoken=page2",
             "value":[
              {"id":"a1","principalId":"u1","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Assigned","memberType":"Direct",
               "startDateTime":"2025-01-01T00:00:00Z","endDateTime":null,
               "principal":{"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Sam Chen","userPrincipalName":"sam.chen@contoso.com","userType":"Member","accountEnabled":true}},
              {"id":"a3","principalId":"u2","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Activated","memberType":"Direct",
               "startDateTime":"2026-09-13T08:00:00Z","endDateTime":"2026-09-13T16:00:00Z",
               "principal":{"@odata.type":"#microsoft.graph.user","id":"u2","displayName":"Priya Natarajan","userPrincipalName":"priya.natarajan_fabrikam.com#EXT#@contoso.com","userType":"Guest"}}
            ]}
            """);
        stub.On("GET", "roleEligibilityScheduleInstances", """
            {"value":[
              {"id":"e1","principalId":"u2","roleDefinitionId":"rd-ga","directoryScopeId":"/","memberType":"Direct","startDateTime":"2025-01-01T00:00:00Z","endDateTime":null,
               "principal":{"@odata.type":"#microsoft.graph.user","id":"u2","displayName":"Priya Natarajan","userType":"Guest"}}
            ]}
            """);

        var data = await new DirectoryRoleCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(CancellationToken.None);

        data.Definitions.Should().ContainSingle(d => d.Id == "rd-ga").Which.IsPrivileged.Should().BeTrue("the catalogue marks Global Administrator privileged when Graph omits the flag");
        data.Definitions.Should().ContainSingle(d => d.Id == "rd-reader").Which.IsPrivileged.Should().BeFalse();
        data.Assignments.Select(a => a.Id).Should().Equal("a1", "a3", "a2");
        data.Assignments.Single(a => a.Id == "a1").IsPermanent.Should().BeTrue();
        data.Assignments.Single(a => a.Id == "a3").IsPermanent.Should().BeFalse();
        data.Assignments.Single(a => a.Id == "a3").AssignmentType.Should().Be(AssignmentType.Activated);
        data.Eligibilities.Should().ContainSingle().Which.AssignmentType.Should().BeNull();
        data.Principals.Should().HaveCount(3);
        data.Principals.Single(p => p.Id == "u2").Should().BeEquivalentTo(new { Type = PrincipalType.User, IsGuest = true, DisplayName = "Priya Natarajan" });
        data.Principals.Single(p => p.Id == "g1").Type.Should().Be(PrincipalType.Group);
        stub.RequestsMatching("roleAssignmentScheduleInstances").Should().HaveCount(2);
    }
}
