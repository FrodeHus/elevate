import Testing
import Foundation
@testable import ElevateCore

@Suite struct PanelFilterTests {
    let role = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/s1", roleDefinitionId: "r")),
                            displayName: "Contributor", detail: "Pay-As-You-Go · subscription", source: .discovered, policy: .manualDefault, viaGroup: "Platform Team")

    @Test func emptyQueryMatchesEverything() {
        #expect(PanelFilter.matches(query: "", role: role, tenantName: "Contoso", upn: "u@contoso.com"))
        #expect(PanelFilter.matches(query: "   ", role: role, tenantName: "Contoso", upn: "u@contoso.com"))
        #expect(!PanelFilter.isActive("  "))
        #expect(PanelFilter.isActive(" x"))
    }

    @Test func matchesEachFieldCaseInsensitively() {
        for q in ["contrib", "CONTRIBUTOR", "pay-as", "platform", "contoso", "U@CONTOSO"] {
            #expect(PanelFilter.matches(query: q, role: role, tenantName: "Contoso", upn: "u@contoso.com"), "query \(q)")
        }
        #expect(!PanelFilter.matches(query: "reader", role: role, tenantName: "Contoso", upn: "u@contoso.com"))
    }

    @Test func textOverloadTrimsWhitespaceAndNewlines() {
        #expect(PanelFilter.matches(query: "owner\n", text: "Group owner"))
        #expect(PanelFilter.matches(query: " \n ", text: "anything"))
        #expect(!PanelFilter.matches(query: "reader\n", text: "Group owner"))
    }

    @Test func ignoresDiacritics() {
        var r = role
        r.displayName = "Sécurité"
        #expect(PanelFilter.matches(query: "securite", role: r, tenantName: "", upn: ""))
    }
}
