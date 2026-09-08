import Testing
import Foundation
@testable import ElevateCore

@Suite struct AccessTokenClaimsTests {
    private func token(scp: String?) -> String {
        var payload: [String: Any] = ["aud": "https://graph.microsoft.com", "tid": "t"]
        if let scp { payload["scp"] = scp }
        let body = try! JSONSerialization.data(withJSONObject: payload)
        let b64 = body.base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
        return "eyJhbGciOiJub25lIn0.\(b64).sig"
    }

    @Test func readsScopesFromScpClaim() {
        let scopes = AccessTokenClaims.grantedScopes(token(scp: "User.Read RoleEligibilitySchedule.Read.Directory"))
        #expect(scopes == ["User.Read", "RoleEligibilitySchedule.Read.Directory"])
    }

    @Test func readOnlyTokenDoesNotPermitActivation() {
        #expect(AccessTokenClaims.permitsEntraActivation(token(scp: "User.Read RoleEligibilitySchedule.Read.Directory RoleAssignmentSchedule.Read.Directory")) == false)
    }

    @Test func anyWriteScopePermitsActivation() {
        for scope in ["RoleAssignmentSchedule.ReadWrite.Directory", "RoleManagement.ReadWrite.Directory", "PrivilegedAccess.ReadWrite.AzureAD"] {
            #expect(AccessTokenClaims.permitsEntraActivation(token(scp: "User.Read \(scope)")) == true)
        }
    }

    @Test func opaqueOrScopelessTokenIsUnknown() {
        #expect(AccessTokenClaims.permitsEntraActivation("not-a-jwt") == nil)
        #expect(AccessTokenClaims.permitsEntraActivation(token(scp: nil)) == nil)
    }

    @Test func firstPartyMethodsAreViewOnlyForEntra() {
        #expect(SignInMethod.ownApp.isPreauthorisedForEntraActivation)
        #expect(SignInMethod.ownApp.entraViewOnlyReason == nil)
        for m in [SignInMethod.azureCLI, .azurePowerShell] {
            #expect(!m.isPreauthorisedForEntraActivation)
            #expect(m.limitationSummary?.contains("Azure resource roles only") == true)
            #expect(m.entraViewOnlyReason?.contains(m.displayName) == true)
        }
    }

    @Test func tenantContextDecodesWithoutEntraActivation() throws {
        let json = #"{"identityId":"i","tenantId":"t","displayName":"T","source":"home","discoveryMode":"automatic"}"#
        let t = try JSONDecoder().decode(TenantContext.self, from: Data(json.utf8))
        #expect(t.entraActivation == nil)
        var u = t
        u.entraActivation = .unsupported(reason: "no")
        let round = try JSONDecoder().decode(TenantContext.self, from: JSONEncoder().encode(u))
        #expect(round.entraActivation?.reason == "no")
    }

    @Test func entitlementSelfServiceScopeIsDetected() {
        #expect(AccessTokenClaims.permitsEntitlementSelfService(token(scp: "User.Read EntitlementMgmt-SubjectAccess.ReadWrite")) == true)
        #expect(AccessTokenClaims.permitsEntitlementSelfService(token(scp: "User.Read RoleAssignmentSchedule.ReadWrite.Directory")) == false)
        #expect(AccessTokenClaims.permitsEntitlementSelfService("opaque-token") == nil)
    }

    @Test func entitlementScopeConstant() {
        #expect(EntitlementScopes.all == ["https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite"])
    }

    @Test func tenantContextAccessPackagesFlagDefaultsToNilAndRoundTrips() throws {
        var t = TenantContext(identityId: "i", tenantId: "t", displayName: "Contoso", source: .home)
        #expect(t.accessPackagesAvailable == nil)
        t.accessPackagesAvailable = true
        let data = try JSONEncoder().encode(t)
        #expect(try JSONDecoder().decode(TenantContext.self, from: data).accessPackagesAvailable == true)
        // A state file written before this field existed still loads.
        let legacy = Data(#"{"identityId":"i","tenantId":"t","displayName":"Contoso","source":"home","discoveryMode":"automatic"}"#.utf8)
        #expect(try JSONDecoder().decode(TenantContext.self, from: legacy).accessPackagesAvailable == nil)
    }
}
