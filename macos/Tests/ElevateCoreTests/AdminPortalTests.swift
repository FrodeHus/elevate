import Testing
import Foundation
@testable import ElevateCore

@Suite struct AdminPortalTests {
    let tenant = "11111111-2222-3333-4444-555555555555"

    @Test func ibizaPortalsTakeTheTenantInThePath() {
        #expect(AdminPortal.azure.url(tenantId: tenant).absoluteString == "https://portal.azure.com/\(tenant)")
        #expect(AdminPortal.entra.url(tenantId: tenant).absoluteString == "https://entra.microsoft.com/\(tenant)")
        #expect(AdminPortal.intune.url(tenantId: tenant).absoluteString == "https://intune.microsoft.com/\(tenant)")
    }

    @Test func securityPortalsTakeTheTenantAsQuery() {
        #expect(AdminPortal.defender.url(tenantId: tenant).absoluteString == "https://security.microsoft.com/?tid=\(tenant)")
        #expect(AdminPortal.purview.url(tenantId: tenant).absoluteString == "https://purview.microsoft.com/?tid=\(tenant)")
    }

    @Test func tenantIdIsEscaped() {
        #expect(AdminPortal.azure.url(tenantId: "a b/c").absoluteString == "https://portal.azure.com/a%20b%2Fc")
        #expect(AdminPortal.defender.url(tenantId: "a&b").absoluteString == "https://security.microsoft.com/?tid=a%26b")
    }

    @Test func menuOrderAndTitles() {
        #expect(AdminPortal.allCases == [.azure, .entra, .intune, .defender, .purview])
        #expect(AdminPortal.allCases.map(\.title) == ["Azure Portal", "Entra admin center", "Intune admin center", "Defender Portal", "Purview Portal"])
    }
}
