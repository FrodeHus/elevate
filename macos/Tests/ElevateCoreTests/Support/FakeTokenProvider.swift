import Foundation
import ElevateCore

actor FakeTokenProvider: TokenProviding {
    struct InteractiveCall: Equatable { let tenantId: String; let scopes: [String]; let claims: String? }
    var storedIdentities: [Identity] = []
    var silentError: PIMError?
    var interactiveError: PIMError?
    private(set) var interactiveCalls: [InteractiveCall] = []
    private(set) var silentCalls: [String] = []
    /// Identity ids passed to `signOut`, so a test can assert a sign-in was (or was not) discarded.
    private(set) var signOutCalls: [String] = []

    func setSilentError(_ e: PIMError?) { silentError = e }
    func setInteractiveError(_ e: PIMError?) { interactiveError = e }

    func signIn(method: SignInMethod) async throws -> Identity {
        let i = Identity(id: "new", upn: "new@x", displayName: "New", homeTenantId: "home", signInMethod: method)
        storedIdentities.append(i)
        return i
    }
    func signOut(_ identity: Identity) async throws {
        signOutCalls.append(identity.id)
        storedIdentities.removeAll { $0.id == identity.id }
    }
    func identities() async throws -> [Identity] { storedIdentities }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        silentCalls.append(tenantId)
        if let silentError { throw silentError }
        return "token-\(tenantId)"
    }

    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        interactiveCalls.append(InteractiveCall(tenantId: tenantId, scopes: scopes, claims: claims))
        if let interactiveError { throw interactiveError }
        silentError = nil
        return "token-\(tenantId)"
    }
}
