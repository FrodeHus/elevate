using Elevate.App.Auth;
using Elevate.App.Tests.Support;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class CompositeTokenProviderTests
{
    private static readonly Identity OwnAppIdentity = Sample.Identity("own", SignInMethod.OwnApp);
    private static readonly Identity CliIdentity = Sample.Identity("cli", SignInMethod.AzureCLI);

    [Fact]
    public async Task RoutesByTheIdentitysSignInMethod()
    {
        var own = new FakeOwnAppProvider();
        var first = new FakeTokenProvider();
        var composite = new CompositeTokenProvider(own, new FakeFirstPartyProviders(first));

        await composite.AccessTokenAsync(OwnAppIdentity, "t1", Scopes.GraphAll, CancellationToken.None);
        await composite.AccessTokenAsync(CliIdentity, "t2", Scopes.ArmAll, CancellationToken.None);

        own.Inner.SilentCalls.Should().Equal("t1");
        first.SilentCalls.Should().Equal("t2");
    }

    [Fact]
    public async Task SignInGoesToTheProviderOfTheMethod()
    {
        var own = new FakeOwnAppProvider();
        var first = new FakeTokenProvider();
        var composite = new CompositeTokenProvider(own, new FakeFirstPartyProviders(first));

        var identity = await composite.SignInAsync(SignInMethod.AzureCLI, CancellationToken.None);

        identity.SignInMethod.Should().Be(SignInMethod.AzureCLI);
        first.StoredIdentities.Should().ContainSingle();
        own.Inner.StoredIdentities.Should().BeEmpty();
    }

    [Fact]
    public async Task IdentitiesUnionEveryProviderDistinctById()
    {
        var own = new FakeOwnAppProvider();
        own.Inner.AddIdentity(OwnAppIdentity);
        own.Inner.AddIdentity(Sample.Identity("shared", SignInMethod.OwnApp));
        var first = new FakeTokenProvider();
        first.AddIdentity(CliIdentity);
        first.AddIdentity(Sample.Identity("shared", SignInMethod.AzureCLI));
        var composite = new CompositeTokenProvider(own, new FakeFirstPartyProviders(first));

        var identities = await composite.IdentitiesAsync(CancellationToken.None);

        identities.Select(i => i.Id).Should().BeEquivalentTo(["own", "shared", "cli"]);
    }

    [Fact]
    public async Task OwnAppWithoutAProviderFailsWithAConfigurationMessage()
    {
        var composite = new CompositeTokenProvider(null, new FakeFirstPartyProviders(new FakeTokenProvider()));

        var act = () => composite.SignInAsync(SignInMethod.OwnApp, CancellationToken.None);

        (await act.Should().ThrowAsync<PimException>()).Which.UserMessage.Should().Contain("client id");
    }

    [Fact]
    public async Task UnknownFirstPartyMethodIsRefused()
    {
        var composite = new CompositeTokenProvider(new FakeOwnAppProvider(), new FakeFirstPartyProviders(null));

        var act = () => composite.AccessTokenAsync(CliIdentity, "t", Scopes.ArmAll, CancellationToken.None);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.Unexpected);
    }

    [Fact]
    public void FirstPartyScopesCollapseToTheResourceDefault()
    {
        FirstPartyTokenProvider.ResourceDefault(Scopes.GraphAll).Should().Be("https://graph.microsoft.com/.default");
        FirstPartyTokenProvider.ResourceDefault(Scopes.ArmAll).Should().Be("https://management.azure.com/.default");
        FirstPartyTokenProvider.ResourceDefault(["scope"]).Should().Be("https://graph.microsoft.com/.default");
    }

    [Fact]
    public void MsalErrorsMapOntoThePimKinds()
    {
        MsalProviderBase.Map(new Microsoft.Identity.Client.MsalUiRequiredException("code", "interaction")).Kind
            .Should().Be(PimErrorKind.InteractionRequired);
        MsalProviderBase.Map(new Microsoft.Identity.Client.MsalClientException(
            Microsoft.Identity.Client.MsalError.AuthenticationCanceledError, "cancelled")).UserMessage
            .Should().Be("Network error: Sign-in cancelled");
        MsalProviderBase.Map(new Microsoft.Identity.Client.MsalServiceException("invalid_grant", "AADSTS65001: consent")).Kind
            .Should().Be(PimErrorKind.ConsentRequired);
        MsalProviderBase.Map(new Microsoft.Identity.Client.MsalServiceException("x", "AADSTS50011: redirect\nTrace ID: 1")).UserMessage
            .Should().Be("Network error: AADSTS50011: redirect");
    }
}
