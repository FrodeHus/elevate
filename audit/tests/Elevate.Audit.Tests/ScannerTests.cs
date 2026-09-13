using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ScannerTests
{
    private static StubHttpClient Tenant()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/organization", """{"value":[{"id":"11111111-1111-1111-1111-111111111111","displayName":"Contoso"}]}""");
        stub.On("GET", "/roleManagement/directory/roleDefinitions", """{"value":[{"id":"rd-ga","templateId":"62e90394-69f5-4237-9190-012177145e10","displayName":"Global Administrator","isPrivileged":true,"isBuiltIn":true}]}""");
        stub.On("GET", "roleAssignmentScheduleInstances?$expand", """
            {"value":[{"id":"a1","principalId":"g1","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Assigned","memberType":"Direct","principal":{"@odata.type":"#microsoft.graph.group","id":"g1","displayName":"Tier 0 Admins"}}]}
            """);
        stub.On("GET", "roleEligibilityScheduleInstances", """{"value":[]}""");
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[]}]}""");
        stub.On("GET", "/groups/g1/members", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Sam Chen","userPrincipalName":"sam.chen@contoso.com","userType":"Member"}]}""");
        stub.On("GET", "privilegedAccess/group", """{"value":[]}""");
        stub.On("POST", "/directoryObjects/getByIds", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u9","displayName":"Jordan Lee","userPrincipalName":"jordan.lee@contoso.com","userType":"Member"}]}""");
        stub.On("GET", "/providers/Microsoft.Management/managementGroups?", """{"value":[]}""");
        stub.On("GET", "/subscriptions?api-version", """{"value":[{"id":"/subscriptions/sub1","subscriptionId":"sub1","displayName":"Production"}]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments?", """
            {"value":[{"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments/ra1","name":"ra1","properties":{"scope":"/subscriptions/sub1","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","principalId":"u9","principalType":"User"}}]}
            """);
        stub.On("GET", "roleAssignmentScheduleInstances?api-version", """{"value":[]}""");
        stub.On("GET", "roleEligibilityScheduleInstances?api-version", """{"value":[]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions?", """{"value":[{"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","name":"8e3af657-a8ff-443c-a75c-2fe8c4bcb635","properties":{"roleName":"Owner","type":"BuiltInRole","permissions":[{"actions":["*"]}]}}]}""");
        return stub;
    }

    private static Scanner Build(StubHttpClient stub, AuditOptions? options = null, bool withArm = true) => new(
        TestIdentity.Graph(stub),
        withArm ? TestIdentity.Arm(stub) : null,
        TestIdentity.Alex,
        TestIdentity.TenantId,
        options ?? new AuditOptions(),
        "1.2.3",
        _ => { },
        new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T12:00:00Z")));

    [Fact]
    public async Task Scan_AssemblesTheSnapshot_AndResolvesUnknownPrincipals()
    {
        var stub = Tenant();

        var snapshot = await Build(stub).ScanAsync(CancellationToken.None);

        snapshot.Kind.Should().Be(Snapshot.KindMarker);
        snapshot.ToolVersion.Should().Be("1.2.3");
        snapshot.Tenant.Should().Be(new TenantInfo(TestIdentity.TenantId, "Contoso"));
        snapshot.Account.Should().Be("alex.rivera@contoso.com");
        snapshot.ScannedAt.Should().Be(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        snapshot.EntraAssignments.Should().ContainSingle();
        snapshot.Groups.Should().ContainSingle().Which.DirectMembers.Should().ContainSingle();
        snapshot.Principals.Select(p => p.Id).Should().BeEquivalentTo(["g1", "u1", "u9"], "u9 came only from ARM and was resolved through getByIds");
        snapshot.AzureAssignments.Should().ContainSingle();
        snapshot.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_WhenAzureFails_SkipsTheSourceAndContinues()
    {
        var stub = Tenant();
        stub.On("GET", "/subscriptions?api-version", """{"error":{"code":"Boom","message":"ARM is down"}}""", 500);

        var snapshot = await Build(stub).ScanAsync(CancellationToken.None);

        snapshot.Skipped.Should().ContainSingle(s => s.Source == "azure").Which.Reason.Should().Contain("ARM is down");
        snapshot.AzureAssignments.Should().BeEmpty();
        snapshot.EntraAssignments.Should().ContainSingle();
    }

    [Fact]
    public async Task Scan_WithSkipAzure_NeverCallsArm()
    {
        var stub = Tenant();

        await Build(stub, new AuditOptions(SkipAzure: true)).ScanAsync(CancellationToken.None);

        stub.RequestsMatching("management.azure.com").Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_WhenDirectoryRolesAreForbidden_Throws()
    {
        var stub = Tenant();
        stub.On("GET", "roleAssignmentScheduleInstances?$expand", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", 403);

        var act = () => Build(stub).ScanAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.Forbidden);
    }
}
