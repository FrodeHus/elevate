using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>The admin portals the tenant menu can open directly in a tenant.</summary>
public class AdminPortalTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void IbizaPortalsTakeTheTenantInThePath()
    {
        AdminPortal.Azure.Uri(Tenant).AbsoluteUri.Should().Be($"https://portal.azure.com/{Tenant}");
        AdminPortal.Entra.Uri(Tenant).AbsoluteUri.Should().Be($"https://entra.microsoft.com/{Tenant}");
        AdminPortal.Intune.Uri(Tenant).AbsoluteUri.Should().Be($"https://intune.microsoft.com/{Tenant}");
    }

    [Fact]
    public void SecurityPortalsTakeTheTenantAsQuery()
    {
        AdminPortal.Defender.Uri(Tenant).AbsoluteUri.Should().Be($"https://security.microsoft.com/?tid={Tenant}");
        AdminPortal.Purview.Uri(Tenant).AbsoluteUri.Should().Be($"https://purview.microsoft.com/?tid={Tenant}");
    }

    [Fact]
    public void TenantIdIsEscaped()
    {
        AdminPortal.Azure.Uri("a b/c").AbsoluteUri.Should().Be("https://portal.azure.com/a%20b%2Fc");
        AdminPortal.Defender.Uri("a&b").AbsoluteUri.Should().Be("https://security.microsoft.com/?tid=a%26b");
    }

    [Fact]
    public void MenuOrderAndTitles()
    {
        AdminPortal.All.Should().Equal(AdminPortal.Azure, AdminPortal.Entra, AdminPortal.Intune, AdminPortal.Defender, AdminPortal.Purview);
        AdminPortal.All.Select(p => p.Title).Should().Equal("Azure Portal", "Entra admin center", "Intune admin center", "Defender Portal", "Purview Portal");
    }

    [Fact]
    public void RejectsBlankTenant()
    {
        var act = () => AdminPortal.Azure.Uri(" ");
        act.Should().Throw<ArgumentException>();
    }
}
