using Elevate.Audit.Collectors;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class GraphUrlsTests
{
    [Fact]
    public void RoleAssignmentInstances_DoesNotExpandRoleDefinition()
    {
        GraphUrls.RoleAssignmentInstances.ToString().Should().Contain("$expand=principal").And.NotContain("roleDefinition");
    }

    [Fact]
    public void RoleEligibilityInstances_DoesNotExpandRoleDefinition()
    {
        GraphUrls.RoleEligibilityInstances.ToString().Should().Contain("$expand=principal").And.NotContain("roleDefinition");
    }

    [Fact]
    public void RoleAssignableGroups_SelectsSecurityMailAndVisibility()
    {
        GraphUrls.RoleAssignableGroups.ToString().Should().Contain("$select=id,displayName,isAssignableToRole,groupTypes,securityEnabled,mailEnabled,visibility");
    }

    [Fact]
    public void Group_SelectsSecurityMailAndVisibility()
    {
        GraphUrls.Group("g1").ToString().Should().Contain("$select=id,displayName,isAssignableToRole,groupTypes,securityEnabled,mailEnabled,visibility");
    }
}
