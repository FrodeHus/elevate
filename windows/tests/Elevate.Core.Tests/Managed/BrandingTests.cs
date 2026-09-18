using Elevate.Core.Managed;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

/// <summary>Port of the Swift <c>BrandingTests</c>; the asserted strings must stay identical.</summary>
public class BrandingTests
{
    private static ManagedConfiguration Config(Dictionary<string, object?> values) =>
        ManagedConfiguration.Load(new DictionaryManagedSource(values));

    [Fact]
    public void UnbrandedResolvesToNull()
    {
        Branding.Resolve(ManagedConfiguration.None).Should().BeNull();
        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["ClientId"] = "11111111-2222-3333-4444-555555555555",
        })).Should().BeNull();
    }

    [Fact]
    public void DefaultStyleIsBy()
    {
        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
        }))!.HeaderCaption.Should().Be("by Contoso");
    }

    [Fact]
    public void ManagedByStyleCaption()
    {
        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
            ["OrganizationTitleStyle"] = "managedBy",
        }))!.HeaderCaption.Should().Be("Managed by Contoso");
    }

    [Fact]
    public void NoneStyleSuppressesTheCaptionOnly()
    {
        var branding = Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
            ["OrganizationTitleStyle"] = "none",
            ["OrganizationSupportUrl"] = "https://help.contoso.com",
        }))!;

        branding.HeaderCaption.Should().BeNull();
        branding.FirstRunLine.Should().Be("Provided by Contoso.");
        branding.HasSupport.Should().BeTrue();
    }

    [Fact]
    public void SupportLinePrefersTheUrl()
    {
        // Uri.ToString() normalises the authority-only form to a trailing slash.
        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
            ["OrganizationSupportUrl"] = "https://help.contoso.com",
            ["OrganizationSupportEmail"] = "it@contoso.com",
        }))!.SupportLine.Should().Be("Need help? Contoso IT — https://help.contoso.com/");

        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
            ["OrganizationSupportEmail"] = "it@contoso.com",
        }))!.SupportLine.Should().Be("Need help? Contoso IT — it@contoso.com");
    }

    [Fact]
    public void SupportDestinationFallsBackToMailto()
    {
        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
            ["OrganizationSupportUrl"] = "https://help.contoso.com",
        }))!.SupportDestination!.ToString().Should().Be("https://help.contoso.com/");

        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
            ["OrganizationSupportEmail"] = "it@contoso.com",
        }))!.SupportDestination!.ToString().Should().Be("mailto:it@contoso.com");

        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
        }))!.SupportDestination.Should().BeNull();
    }

    [Fact]
    public void DiagnosticsLineNamesTheOrganizationAndHelpDesk()
    {
        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
            ["OrganizationTitleStyle"] = "managedBy",
            ["OrganizationSupportUrl"] = "https://help.contoso.com",
        }))!.DiagnosticsLine.Should().Be("Contoso (managedBy) · https://help.contoso.com/");

        Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
        }))!.DiagnosticsLine.Should().Be("Contoso (by)");
    }

    [Fact]
    public void NoSupportMeansNoSupportLine()
    {
        var branding = Branding.Resolve(Config(new Dictionary<string, object?>
        {
            ["OrganizationName"] = "Contoso",
        }))!;

        branding.HasSupport.Should().BeFalse();
        branding.SupportLine.Should().BeNull();
    }
}
