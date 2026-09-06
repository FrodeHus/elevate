using Elevate.Core.Discovery;
using Elevate.Core.Models;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class TenantDiscoveryTests
{
    private static readonly Identity Account = new("id1", "u@contoso.com", "U", "t-home");

    [Fact]
    public async Task ListsTenantsFromArm()
    {
        var http = new StubHttpClient();
        var tokens = new FakeTokenProvider();
        http.On("GET", "management.azure.com/tenants", body: Fixtures.Data("arm-tenants"));
        var discovery = new TenantDiscovery(http, tokens);

        var tenants = await discovery.DiscoverTenantsAsync(Account);

        tenants.Select(t => t.TenantId).Should().Equal("t-home", "t-cust", "t-nodisplay");
        tenants[1].DisplayName.Should().Be("Fabrikam");
        tenants[1].DefaultDomain.Should().Be("fabrikam.onmicrosoft.com");
        tenants[2].DisplayName.Should().Be("t-nodisplay");
        tenants[0].Id.Should().Be("t-home");

        var request = http.Requests[0];
        request.Headers["Authorization"].Should().Be("Bearer token-t-home");
        request.Url.AbsoluteUri.Should().Contain("api-version=2022-12-01");
        tokens.SilentCalls.Should().Equal("t-home");
    }

    [Fact]
    public async Task ResolvesDomainThroughOpenIdConfiguration()
    {
        var http = new StubHttpClient();
        http.On("GET", "fabrikam.com/v2.0/.well-known/openid-configuration",
            """{"issuer":"https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0"}""");
        var discovery = new TenantDiscovery(http, new FakeTokenProvider());

        var id = await discovery.ResolveTenantIdAsync("fabrikam.com");

        id.Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public async Task PassesGuidThroughWithoutNetwork()
    {
        var http = new StubHttpClient();
        var discovery = new TenantDiscovery(http, new FakeTokenProvider());

        var id = await discovery.ResolveTenantIdAsync(" 11111111-2222-3333-4444-AAAAAAAAAAAA ");

        id.Should().Be("11111111-2222-3333-4444-aaaaaaaaaaaa");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PercentEncodesTheDomainInTheDiscoveryUrl()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration",
            """{"issuer":"https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0"}""");
        var discovery = new TenantDiscovery(http, new FakeTokenProvider());

        await discovery.ResolveTenantIdAsync("contoso ltd.com");

        http.Requests[0].Url.AbsoluteUri.Should().Contain("/contoso%20ltd.com/v2.0/");
    }

    [Fact]
    public async Task UnknownDomainThrows()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", """{"error":"invalid_tenant"}""", status: 400);
        var discovery = new TenantDiscovery(http, new FakeTokenProvider());

        var error = await discovery.Invoking(d => d.ResolveTenantIdAsync("nope.example"))
            .Should().ThrowAsync<PimException>();

        error.Which.Kind.Should().Be(PimErrorKind.Unexpected);
        error.Which.Detail.Should().Contain("nope.example");
    }

    [Fact]
    public async Task IssuerWithoutATenantIdThrows()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", """{"issuer":"https://login.microsoftonline.com/common/v2.0"}""");
        var discovery = new TenantDiscovery(http, new FakeTokenProvider());

        await discovery.Invoking(d => d.ResolveTenantIdAsync("common.example")).Should().ThrowAsync<PimException>();
    }

    [Fact]
    public async Task ReadsOrganizationDisplayName()
    {
        var http = new StubHttpClient();
        http.On("GET", "/organization", """{"value":[{"id":"t-cust","displayName":"Fabrikam AS"}]}""");
        var discovery = new TenantDiscovery(http, new FakeTokenProvider());

        var name = await discovery.TenantDisplayNameAsync(Account, "t-cust");

        name.Should().Be("Fabrikam AS");
        http.Requests[0].Url.AbsoluteUri.Should().Contain("/organization?$select=id,displayName");
    }

    [Fact]
    public async Task FallsBackToTheTenantIdWhenOrganizationHasNoName()
    {
        var http = new StubHttpClient();
        http.On("GET", "/organization", """{"value":[]}""");
        var discovery = new TenantDiscovery(http, new FakeTokenProvider());

        (await discovery.TenantDisplayNameAsync(Account, "t-cust")).Should().Be("t-cust");
    }
}
