using Elevate.Audit.Auth;
using Elevate.Core.Models;
using FluentAssertions;
using Microsoft.Identity.Client;

namespace Elevate.Audit.Tests;

public class AuthTests
{
    [Fact]
    public void ScopesFor_GraphWithDefaultClient_AlwaysAsksForTheReadScopes()
    {
        var (resource, scopes) = ClientIds.ScopesFor(["https://graph.microsoft.com/User.Read"], customGraphClient: false);

        resource.Should().Be(Resource.Graph);
        scopes.Should().Equal(ClientIds.GraphReadScopes);
        scopes.Should().HaveCount(7).And.StartWith("https://graph.microsoft.com/User.Read");
    }

    [Fact]
    public void GraphRequiredScopes_AreTheReadScopesWithoutTheOptionalAuditLogOne()
    {
        ClientIds.GraphReadScopes.Should().Contain(ClientIds.ActivationHistoryScope);
        ClientIds.GraphRequiredScopes.Should().NotContain(ClientIds.ActivationHistoryScope).And.HaveCount(6);
    }

    [Fact]
    public void ScopesFor_GraphWithAnExplicitScopeList_AsksForThatListInstead()
    {
        var (_, scopes) = ClientIds.ScopesFor(ClientIds.GraphReadScopes, customGraphClient: false, ClientIds.GraphRequiredScopes);

        scopes.Should().Equal(ClientIds.GraphRequiredScopes);
    }

    [Fact]
    public void ScopesFor_GraphWithCustomClient_AsksForDefault()
    {
        var (_, scopes) = ClientIds.ScopesFor(ClientIds.GraphReadScopes, customGraphClient: true);

        scopes.Should().Equal("https://graph.microsoft.com/.default");
    }

    [Fact]
    public void ScopesFor_Arm_AlwaysAsksForDefault()
    {
        var (resource, scopes) = ClientIds.ScopesFor(["https://management.azure.com/user_impersonation"], customGraphClient: false);

        resource.Should().Be(Resource.Arm);
        scopes.Should().Equal("https://management.azure.com/.default");
    }

    [Fact]
    public void Map_ConsentDeclined_IsConsentRequired()
    {
        var error = MsalErrors.Map(new MsalServiceException("access_denied", "AADSTS65004: User declined to consent to access the app."));

        error.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public void Map_BlockedApp_IsForbiddenWithTheCodeKept()
    {
        var error = MsalErrors.Map(new MsalServiceException("invalid_client", "AADSTS7000112: Application 'x' is disabled."));

        error.Kind.Should().Be(PimErrorKind.Forbidden);
        error.Detail.Should().Contain("AADSTS7000112");
    }

    [Fact]
    public void Explain_ConsentRequired_PointsAtClientIdOverride()
    {
        var text = MsalErrors.Explain(new PimException(PimErrorKind.ConsentRequired), ClientIds.GraphDefault);

        text.Should().Contain("Microsoft Graph Command Line Tools").And.Contain("--client-id").And.Contain("docs/audit.md");
    }

    [Fact]
    public void Explain_Forbidden_NamesTheRolesThatCanRead()
    {
        var text = MsalErrors.Explain(new PimException(PimErrorKind.Forbidden, "Authorization failed"), ClientIds.GraphDefault);

        text.Should().Contain("Global Reader").And.Contain("Privileged Role Administrator");
    }

    [Fact]
    public void Provider_RejectsANonGuidClientId()
    {
        var act = () => new AuditTokenProvider("not-a-guid", "organizations", deviceCode: false, _ => { });

        act.Should().Throw<Elevate.Audit.Infrastructure.AuditException>().WithMessage("*client*GUID*");
    }
}
