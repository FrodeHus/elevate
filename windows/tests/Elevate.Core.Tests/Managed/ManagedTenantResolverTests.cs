using Elevate.Core.Managed;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class ManagedTenantResolverTests
{
    private static string Issuer(string tenantId)
        => $$"""{"issuer":"https://login.microsoftonline.com/{{tenantId}}/v2.0"}""";

    [Fact]
    public async Task GuidPassesThroughWithoutARequest()
    {
        var http = new StubHttpClient();
        var resolver = new ManagedTenantResolver(http);

        var result = await resolver.ResolveAsync([" 11111111-2222-3333-4444-AAAAAAAAAAAA "]);

        result.Ids.Should().Equal(new Dictionary<string, string>
        {
            [" 11111111-2222-3333-4444-AAAAAAAAAAAA "] = "11111111-2222-3333-4444-aaaaaaaaaaaa",
        });
        result.Unresolved.Should().BeEmpty();
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ADomainIsRequestedOnlyOncePerResolver()
    {
        var http = new StubHttpClient();
        http.On("GET", "fabrikam.com/v2.0/.well-known/openid-configuration", Issuer("11111111-2222-3333-4444-555555555555"));
        var resolver = new ManagedTenantResolver(http);

        var first = await resolver.ResolveAsync(["fabrikam.com"]);
        var second = await resolver.ResolveAsync(["fabrikam.com"]);

        first.Ids["fabrikam.com"].Should().Be("11111111-2222-3333-4444-555555555555");
        second.Ids["fabrikam.com"].Should().Be("11111111-2222-3333-4444-555555555555");
        http.RequestsMatching("openid-configuration").Should().ContainSingle();
    }

    [Fact]
    public async Task RepeatedEntriesInOneCallAreRequestedOnce()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", Issuer("11111111-2222-3333-4444-555555555555"));
        var resolver = new ManagedTenantResolver(http);

        var result = await resolver.ResolveAsync(["fabrikam.com", "fabrikam.com"]);

        result.Ids.Should().ContainSingle();
        http.RequestsMatching("openid-configuration").Should().ContainSingle();
    }

    /// <summary>
    /// The policy may say <c>contoso.com</c> where a published profile says <c>Contoso.com</c>.
    /// Both must resolve, and the second spelling must not cost a second request.
    /// </summary>
    [Fact]
    public async Task TwoSpellingsOfADomainResolveWithOneRequest()
    {
        var http = new StubHttpClient();
        http.On("GET", "fabrikam.com/v2.0/.well-known/openid-configuration", Issuer("11111111-2222-3333-4444-555555555555"));
        var resolver = new ManagedTenantResolver(http);

        var result = await resolver.ResolveAsync(["fabrikam.com", "Fabrikam.COM"]);

        result.Ids["fabrikam.com"].Should().Be("11111111-2222-3333-4444-555555555555");
        result.Ids["Fabrikam.COM"].Should().Be("11111111-2222-3333-4444-555555555555");
        result.Unresolved.Should().BeEmpty();
        http.RequestsMatching("openid-configuration").Should().ContainSingle();
    }

    [Fact]
    public async Task UnknownDomainLandsInUnresolved()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", """{"error":"invalid_tenant"}""", status: 404);
        var resolver = new ManagedTenantResolver(http);

        var result = await resolver.ResolveAsync(["nope.example"]);

        result.Ids.Should().BeEmpty();
        result.Unresolved.Should().Equal("nope.example");
    }

    [Fact]
    public async Task AFailureIsNotCachedSoTheNextCallRetries()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", string.Empty, status: 500);
        var resolver = new ManagedTenantResolver(http);

        await resolver.ResolveAsync(["fabrikam.com"]);
        http.On("GET", "openid-configuration", Issuer("11111111-2222-3333-4444-555555555555"));
        var second = await resolver.ResolveAsync(["fabrikam.com"]);

        second.Ids["fabrikam.com"].Should().Be("11111111-2222-3333-4444-555555555555");
        http.RequestsMatching("openid-configuration").Should().HaveCount(2);
    }

    [Fact]
    public async Task MixedInputResolvesWhatItCanAndKeepsUnresolvedOrder()
    {
        var http = new StubHttpClient();
        http.On("GET", "fabrikam.com/v2.0", Issuer("11111111-2222-3333-4444-555555555555"));
        http.On("GET", "nope.example/v2.0", string.Empty, status: 404);
        http.On("GET", "also-bad.example/v2.0", string.Empty, status: 404);
        var resolver = new ManagedTenantResolver(http);

        var result = await resolver.ResolveAsync(
        [
            "nope.example",
            "22222222-3333-4444-5555-666666666666",
            "fabrikam.com",
            "also-bad.example",
        ]);

        result.Ids.Keys.Should().BeEquivalentTo("22222222-3333-4444-5555-666666666666", "fabrikam.com");
        result.Ids["fabrikam.com"].Should().Be("11111111-2222-3333-4444-555555555555");
        result.Ids["22222222-3333-4444-5555-666666666666"].Should().Be("22222222-3333-4444-5555-666666666666");
        result.Unresolved.Should().Equal("nope.example", "also-bad.example");
    }

    [Fact]
    public async Task AnUnparseableBodyIsUnresolved()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", "not json at all");
        var resolver = new ManagedTenantResolver(http);

        (await resolver.ResolveAsync(["broken.example"])).Unresolved.Should().Equal("broken.example");
    }

    [Fact]
    public async Task AnIssuerWithoutATenantIdIsUnresolved()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", """{"issuer":"https://login.microsoftonline.com/common/v2.0"}""");
        var resolver = new ManagedTenantResolver(http);

        (await resolver.ResolveAsync(["common.example"])).Unresolved.Should().Equal("common.example");
    }
}
