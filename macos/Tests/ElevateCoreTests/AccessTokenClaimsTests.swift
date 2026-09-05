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
            #expect(m.limitationSummary?.contains("view only") == true)
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
}
