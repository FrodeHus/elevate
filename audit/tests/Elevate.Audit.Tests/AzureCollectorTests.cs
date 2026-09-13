using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class AzureCollectorTests
{
    private static StubHttpClient Tenant()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/providers/Microsoft.Management/managementGroups?", """{"error":{"code":"AuthorizationFailed","message":"no"}}""", 403);
        stub.On("GET", "/subscriptions?api-version", """{"value":[{"id":"/subscriptions/sub1","subscriptionId":"sub1","displayName":"Production"}]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments?", """
            {"value":[
              {"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments/ra1","name":"ra1","properties":{"scope":"/subscriptions/sub1","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","principalId":"u1","principalType":"User"}},
              {"id":"/subscriptions/sub1/resourceGroups/rg-app/providers/Microsoft.Authorization/roleAssignments/ra2","name":"ra2","properties":{"scope":"/subscriptions/sub1/resourceGroups/rg-app","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c","principalId":"g1","principalType":"Group"}}
            ]}
            """);
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignmentScheduleInstances?", """
            {"value":[{"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignmentScheduleInstances/si1","name":"si1","properties":{"scope":"/subscriptions/sub1","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","principalId":"u1","principalType":"User","assignmentType":"Assigned","startDateTime":"2025-01-01T00:00:00Z","endDateTime":null}}]}
            """);
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?", """{"value":[]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions?", """
            {"value":[
              {"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","name":"8e3af657-a8ff-443c-a75c-2fe8c4bcb635","properties":{"roleName":"Owner","type":"BuiltInRole","permissions":[{"actions":["*"]}]}},
              {"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c","name":"b24988ac-6180-42a0-ab88-20f7382dd24c","properties":{"roleName":"Contributor","type":"BuiltInRole","permissions":[{"actions":["*"],"notActions":["Microsoft.Authorization/*/Write"]}]}}
            ]}
            """);
        return stub;
    }

    [Fact]
    public async Task Collect_DegradesWithoutManagementGroups_AndMapsEverything()
    {
        var stub = Tenant();

        var data = await new AzureCollector(TestIdentity.Arm(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(CancellationToken.None);

        data.Notes.Should().ContainSingle().Which.Should().Contain("management groups");
        data.Scopes.Should().ContainSingle().Which.Should().BeEquivalentTo(new AzureScopeRecord("/subscriptions/sub1", AzureScopeKind.Subscription, "Production"));
        data.Assignments.Should().HaveCount(3);
        data.Assignments.Single(a => a.Id.EndsWith("/ra2", StringComparison.Ordinal)).Should().BeEquivalentTo(new { PrincipalId = "g1", PrincipalType = "Group", FromSchedule = false, Scope = "/subscriptions/sub1/resourceGroups/rg-app" });
        data.Assignments.Single(a => a.FromSchedule).AssignmentType.Should().Be(AssignmentType.Assigned);
        data.RoleDefinitions.Should().HaveCount(2);
        data.RoleDefinitions.Single(r => r.DisplayName == "Owner").Actions.Should().Equal("*");
    }

    [Fact]
    public async Task Collect_WithNoSubscriptions_ThrowsSoTheScannerSkipsTheSource()
    {
        var stub = Tenant();
        stub.On("GET", "/subscriptions?api-version", """{"value":[]}""");

        var act = () => new AzureCollector(TestIdentity.Arm(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.NotEligible);
    }

    [Theory]
    [InlineData("/providers/Microsoft.Management/managementGroups/contoso", AzureScopeKind.ManagementGroup, "contoso")]
    [InlineData("/subscriptions/sub1", AzureScopeKind.Subscription, "sub1")]
    [InlineData("/subscriptions/sub1/resourceGroups/rg-app", AzureScopeKind.ResourceGroup, "rg-app")]
    [InlineData("/subscriptions/sub1/resourceGroups/rg-app/providers/Microsoft.KeyVault/vaults/kv-prod", AzureScopeKind.Resource, "kv-prod")]
    public void ScopeKindOf_ClassifiesByPath(string scope, AzureScopeKind kind, string name)
    {
        AzureCollector.ScopeKindOf(scope).Should().Be(kind);
        AzureCollector.ScopeDisplayName(scope).Should().Be(name);
    }
}
