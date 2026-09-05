import Testing
import Foundation
@testable import ElevateCore

@Suite struct TenantDiscoveryTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")

    @Test func listsTenantsFromARM() async throws {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        await http.on("GET", "management.azure.com/tenants", body: Fixtures.data("arm-tenants"))
        let d = TenantDiscovery(http: http, tokens: tokens)
        let tenants = try await d.discoverTenants(identity: identity)
        #expect(tenants.map(\.tenantId) == ["t-home", "t-cust", "t-nodisplay"])
        #expect(tenants[1].displayName == "Fabrikam")
        #expect(tenants[2].displayName == "t-nodisplay")
        let req = await http.requests.first!
        #expect(req.headers["Authorization"] == "Bearer token-t-home")
        #expect(req.url.absoluteString.contains("api-version=2022-12-01"))
        #expect(await tokens.silentCalls == ["t-home"])
    }

    @Test func resolvesDomainThroughOpenIdConfiguration() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "fabrikam.com/v2.0/.well-known/openid-configuration",
                      body: Data(#"{"issuer":"https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0"}"#.utf8))
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        #expect(try await d.resolveTenantId(domainOrId: "fabrikam.com") == "11111111-2222-3333-4444-555555555555")
    }

    @Test func passesGuidThroughWithoutNetwork() async throws {
        let http = StubHTTPClient()
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        #expect(try await d.resolveTenantId(domainOrId: " 11111111-2222-3333-4444-555555555555 ") == "11111111-2222-3333-4444-555555555555")
        #expect(await http.requests.isEmpty)
    }

    @Test func unknownDomainThrows() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "openid-configuration", status: 400, body: Data(#"{"error":"invalid_tenant"}"#.utf8))
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        await #expect(throws: PIMError.self) { _ = try await d.resolveTenantId(domainOrId: "nope.example") }
    }

    @Test func readsOrganizationDisplayName() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "/organization", body: Data(#"{"value":[{"id":"t-cust","displayName":"Fabrikam AS"}]}"#.utf8))
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        #expect(try await d.tenantDisplayName(identity: identity, tenantId: "t-cust") == "Fabrikam AS")
    }
}
