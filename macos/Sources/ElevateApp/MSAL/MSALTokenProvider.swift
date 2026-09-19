import Foundation
import MSAL
import ElevateCore

/// MSALAccount is an immutable value snapshot from MSAL's cache; it is safe to pass across
/// isolation domains, but the ObjC SDK does not annotate it as Sendable itself.
extension MSALAccount: @unchecked @retroactive Sendable {}

/// Wraps MSAL for macOS behind `TokenProviding`. All interactive calls hop to the main actor.
final class MSALTokenProvider: TokenProviding, @unchecked Sendable {
    private let app: MSALPublicClientApplication
    private let anchor: AuthAnchorWindow
    /// Shared with the loopback providers so an MSAL webview and a browser flow cannot run at once.
    private let gate: InteractiveGate
    /// Tokens from a claims (step-up) acquisition. MSAL bypasses its own access-token cache when a
    /// claims request is specified, so the silent call that follows the step-up can hand back the
    /// token from before it; the retry would then be refused for the same missing claim.
    private let stepUp = StepUpTokenCache()
    /// The method stamped on the identities this provider returns: `.ownApp` for the Settings
    /// registration, `.pinnedApp` for a registration an account was added with.
    let method: SignInMethod

    init(method: SignInMethod = .ownApp, clientId: String, redirectUri: String, anchor: AuthAnchorWindow,
         gate: InteractiveGate = InteractiveGate()) throws {
        precondition(method.isOwnApp, "MSAL serves only Entra app registrations, got \(method)")
        let authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/organizations")!)
        let config = MSALPublicClientApplicationConfig(clientId: clientId, redirectUri: redirectUri, authority: authority)
        // "cp1" tells Entra, and the resource, that this client understands a claims challenge and
        // will re-acquire against it. PIM refuses to honour an authentication context (`acrs`) from
        // a client that has not said so: it answers the activation with
        // RoleAssignmentRequestAcrsValidationFailed and re-issues the same challenge, however many
        // times the token is re-minted. MSAL turns this into the token's `xms_cc` claim.
        config.clientApplicationCapabilities = [ClaimsChallenge.clientCapability]
        app = try MSALPublicClientApplication(configuration: config)
        self.anchor = anchor
        self.gate = gate
        self.method = method
    }

    // MARK: TokenProviding

    func signIn(method: SignInMethod) async throws -> Identity {
        guard method == self.method else {
            throw PIMError.unexpected(status: 0, body: "MSAL only signs in with an Entra app registration")
        }
        return try await gate.run { [self] in
            let result = try await interactive(account: nil, tenantId: nil, scopes: [GraphScopes.userRead] + EntitlementScopes.all, claims: nil, prompt: .selectAccount)
            return Self.identity(from: result.account, method: self.method)
        }
    }

    func signOut(_ identity: Identity) async throws {
        await stepUp.forget(identityId: identity.id)
        guard let account = try? app.account(forIdentifier: identity.id) else { return }
        try await gate.run { [self] in
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                Task { @MainActor in
                    let web = MSALWebviewParameters(authPresentationViewController: anchor.present())
                    let params = MSALSignoutParameters(webviewParameters: web)
                    params.signoutFromBrowser = false
                    app.signout(with: account, signoutParameters: params) { _, error in
                        Task { @MainActor in self.anchor.dismiss() }
                        if let error { cont.resume(throwing: Self.map(error)) } else { cont.resume() }
                    }
                }
            }
        }
    }

    /// Drops the given accounts from this client's local MSAL cache without any webview.
    /// Used when the client id changes: the cached tokens belong to the old client and a browser
    /// sign-out would only interrupt the user.
    func removeCachedAccounts(_ identities: [Identity]) throws {
        for identity in identities {
            Task { await stepUp.forget(identityId: identity.id) }
            guard let account = try? app.account(forIdentifier: identity.id) else { continue }
            try app.remove(account)
        }
    }

    func identities() async throws -> [Identity] {
        try app.allAccounts().map { Self.identity(from: $0, method: method) }
    }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        try await accessToken(identity: identity, tenantId: tenantId, scopes: scopes, forceRefresh: false)
    }

    /// MSAL can be told to go back to the token endpoint, which is what a propagation probe needs.
    var canForceRefresh: Bool { true }

    func accessToken(identity: Identity, tenantId: String, scopes: [String], forceRefresh: Bool) async throws -> String {
        // A forced refresh wants a token minted now, so it skips the step-up token as it skips MSAL's cache.
        if !forceRefresh,
           let stepped = await stepUp.token(identityId: identity.id, tenantId: tenantId, scopes: scopes) {
            return stepped
        }
        guard let account = try? app.account(forIdentifier: identity.id) else { throw PIMError.interactionRequired }
        let params = MSALSilentTokenParameters(scopes: scopes, account: account)
        params.authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/\(tenantId)")!)
        params.forceRefresh = forceRefresh
        return try await withCheckedThrowingContinuation { cont in
            app.acquireTokenSilent(with: params) { result, error in
                if let result { cont.resume(returning: result.accessToken) } else { cont.resume(throwing: Self.map(error)) }
            }
        }
    }

    @discardableResult
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        let account = try? app.account(forIdentifier: identity.id)
        return try await gate.run { [self] in
            let result = try await interactive(account: account, tenantId: tenantId, scopes: scopes, claims: claims, prompt: .promptIfNecessary)
            // Only a claims acquisition is worth holding: a plain one has nothing MSAL's cache lacks.
            if claims != nil {
                await stepUp.store(result.accessToken, identityId: identity.id, tenantId: tenantId, scopes: scopes)
            }
            return result.accessToken
        }
    }

    // MARK: Helpers

    private func interactive(account: MSALAccount?, tenantId: String?, scopes: [String], claims: String?, prompt: MSALPromptType) async throws -> MSALResult {
        try await withCheckedThrowingContinuation { cont in
            Task { @MainActor in
                do {
                    let web = MSALWebviewParameters(authPresentationViewController: anchor.present())
                    web.webviewType = .authenticationSession
                    web.prefersEphemeralWebBrowserSession = false
                    let params = MSALInteractiveTokenParameters(scopes: scopes, webviewParameters: web)
                    params.account = account
                    params.promptType = prompt
                    if let tenantId {
                        params.authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/\(tenantId)")!)
                    }
                    if let claims {
                        var err: NSError?
                        params.claimsRequest = MSALClaimsRequest(jsonString: claims, error: &err)
                        if let err { throw err }
                    }
                    app.acquireToken(with: params) { result, error in
                        Task { @MainActor in self.anchor.dismiss() }
                        if let result { cont.resume(returning: result) } else { cont.resume(throwing: Self.map(error)) }
                    }
                } catch {
                    anchor.dismiss()
                    cont.resume(throwing: Self.map(error))
                }
            }
        }
    }

    static func identity(from account: MSALAccount, method: SignInMethod) -> Identity {
        let claims = account.accountClaims ?? [:]
        return Identity(id: account.identifier ?? account.username ?? UUID().uuidString,
                        upn: account.username ?? "unknown",
                        displayName: (claims["name"] as? String) ?? account.username ?? "unknown",
                        homeTenantId: account.homeAccountId?.tenantId ?? (claims["tid"] as? String) ?? "",
                        signInMethod: method)
    }

    static func map(_ error: Error?) -> PIMError {
        guard let error else { return .network("Unknown MSAL failure") }
        let ns = error as NSError
        guard ns.domain == MSALErrorDomain else { return .network(ns.localizedDescription) }
        if ns.code == MSALError.interactionRequired.rawValue { return .interactionRequired }
        if ns.code == MSALError.userCanceled.rawValue { return .network("Sign-in cancelled") }
        let desc = (ns.userInfo[MSALErrorDescriptionKey] as? String) ?? ns.localizedDescription
        if desc.contains("AADSTS65001") || desc.contains("AADSTS65004") || desc.contains("consent_required") || desc.contains("AADSTS90094") {
            return .consentRequired
        }
        return .network(desc)
    }
}
