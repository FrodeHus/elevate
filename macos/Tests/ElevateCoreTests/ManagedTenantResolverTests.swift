import Testing
import Foundation
@testable import ElevateCore

@Suite struct ManagedTenantResolverTests {
    private func issuer(_ tenantId: String) -> Data {
        Data(#"{"issuer":"https://login.microsoftonline.com/\#(tenantId)/v2.0"}"#.utf8)
    }

    @Test func guidPassesThroughWithoutARequest() async {
        let http = StubHTTPClient()
        let resolver = ManagedTenantResolver(http: http)

        let result = await resolver.resolve([" 11111111-2222-3333-4444-AAAAAAAAAAAA "])

        #expect(result.ids == [" 11111111-2222-3333-4444-AAAAAAAAAAAA ": "11111111-2222-3333-4444-aaaaaaaaaaaa"])
        #expect(result.unresolved.isEmpty)
        #expect(await http.requests.isEmpty)
    }

    @Test func aDomainIsRequestedOnlyOncePerResolver() async {
        let http = StubHTTPClient()
        await http.on("GET", "fabrikam.com/v2.0/.well-known/openid-configuration",
                      body: issuer("11111111-2222-3333-4444-555555555555"))
        let resolver = ManagedTenantResolver(http: http)

        let first = await resolver.resolve(["fabrikam.com"])
        let second = await resolver.resolve(["fabrikam.com"])

        #expect(first.ids["fabrikam.com"] == "11111111-2222-3333-4444-555555555555")
        #expect(second.ids["fabrikam.com"] == "11111111-2222-3333-4444-555555555555")
        #expect(await http.requests(matching: "openid-configuration").count == 1)
    }

    @Test func repeatedEntriesInOneCallAreRequestedOnce() async {
        let http = StubHTTPClient()
        await http.on("GET", "openid-configuration", body: issuer("11111111-2222-3333-4444-555555555555"))
        let resolver = ManagedTenantResolver(http: http)

        let result = await resolver.resolve(["fabrikam.com", "fabrikam.com"])

        #expect(result.ids.count == 1)
        #expect(await http.requests(matching: "openid-configuration").count == 1)
    }

    /// The policy may say `contoso.com` where a published profile says `Contoso.com`. Both must
    /// resolve, and the second spelling must not cost a second request.
    @Test func twoSpellingsOfADomainResolveWithOneRequest() async {
        let http = StubHTTPClient()
        await http.on("GET", "fabrikam.com/v2.0/.well-known/openid-configuration",
                      body: issuer("11111111-2222-3333-4444-555555555555"))
        let resolver = ManagedTenantResolver(http: http)

        let result = await resolver.resolve(["fabrikam.com", "Fabrikam.COM"])

        #expect(result.ids["fabrikam.com"] == "11111111-2222-3333-4444-555555555555")
        #expect(result.ids["Fabrikam.COM"] == "11111111-2222-3333-4444-555555555555")
        #expect(result.unresolved.isEmpty)
        #expect(await http.requests(matching: "openid-configuration").count == 1)
    }

    @Test func unknownDomainLandsInUnresolved() async {
        let http = StubHTTPClient()
        await http.on("GET", "openid-configuration", status: 404, body: Data(#"{"error":"invalid_tenant"}"#.utf8))
        let resolver = ManagedTenantResolver(http: http)

        let result = await resolver.resolve(["nope.example"])

        #expect(result.ids.isEmpty)
        #expect(result.unresolved == ["nope.example"])
    }

    @Test func aFailureIsNotCachedSoTheNextCallRetries() async {
        let http = StubHTTPClient()
        await http.on("GET", "openid-configuration", status: 500, body: Data())
        let resolver = ManagedTenantResolver(http: http)

        _ = await resolver.resolve(["fabrikam.com"])
        await http.on("GET", "openid-configuration", body: issuer("11111111-2222-3333-4444-555555555555"))
        let second = await resolver.resolve(["fabrikam.com"])

        #expect(second.ids["fabrikam.com"] == "11111111-2222-3333-4444-555555555555")
        #expect(await http.requests(matching: "openid-configuration").count == 2)
    }

    @Test func mixedInputResolvesWhatItCanAndKeepsUnresolvedOrder() async {
        let http = StubHTTPClient()
        await http.on("GET", "fabrikam.com/v2.0", body: issuer("11111111-2222-3333-4444-555555555555"))
        await http.on("GET", "nope.example/v2.0", status: 404, body: Data())
        await http.on("GET", "also-bad.example/v2.0", status: 404, body: Data())
        let resolver = ManagedTenantResolver(http: http)

        let result = await resolver.resolve([
            "nope.example",
            "22222222-3333-4444-5555-666666666666",
            "fabrikam.com",
            "also-bad.example",
        ])

        #expect(Set(result.ids.keys) == ["22222222-3333-4444-5555-666666666666", "fabrikam.com"])
        #expect(result.ids["fabrikam.com"] == "11111111-2222-3333-4444-555555555555")
        #expect(result.ids["22222222-3333-4444-5555-666666666666"] == "22222222-3333-4444-5555-666666666666")
        #expect(result.unresolved == ["nope.example", "also-bad.example"])
    }

    @Test func anUnparseableBodyIsUnresolved() async {
        let http = StubHTTPClient()
        await http.on("GET", "openid-configuration", body: Data("not json at all".utf8))
        let resolver = ManagedTenantResolver(http: http)

        #expect(await resolver.resolve(["broken.example"]).unresolved == ["broken.example"])
    }

    @Test func anIssuerWithoutATenantIdIsUnresolved() async {
        let http = StubHTTPClient()
        await http.on("GET", "openid-configuration",
                      body: Data(#"{"issuer":"https://login.microsoftonline.com/common/v2.0"}"#.utf8))
        let resolver = ManagedTenantResolver(http: http)

        #expect(await resolver.resolve(["common.example"]).unresolved == ["common.example"])
    }
}
