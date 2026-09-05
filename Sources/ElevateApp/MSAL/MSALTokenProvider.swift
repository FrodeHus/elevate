import Foundation
import MSAL
import ElevateCore

/// MSALAccount is an immutable value snapshot from MSAL's cache; it is safe to pass across
/// isolation domains, but the ObjC SDK does not annotate it as Sendable itself.
extension MSALAccount: @unchecked @retroactive Sendable {}

/// Serialises interactive MSAL sessions: MSAL refuses a second one with
/// `MSALErrorInteractiveSessionAlreadyRunning`, so callers queue instead of failing.
private actor InteractiveGate {
    private var busy = false
    private var waiters: [(id: UUID, continuation: CheckedContinuation<Bool, Never>)] = []

    /// Runs `body` with the gate held, throwing `CancellationError` if the wait is cancelled.
    /// `body` must not call another gated method: the gate is not reentrant, so a nested call
    /// would wait forever for the lock its own caller is holding.
    func run<T: Sendable>(_ body: @Sendable () async throws -> T) async throws -> T {
        try await acquire()
        defer { release() }
        return try await body()
    }

    private func acquire() async throws {
        guard busy else { busy = true; return }
        guard !Task.isCancelled else { throw CancellationError() }
        let id = UUID()
        let owned = await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                waiters.append((id, continuation))
            }
        } onCancel: {
            Task { await self.dropWaiter(id) }
        }
        guard owned else { throw CancellationError() }
        // Cancelled just as the gate was handed over: pass it straight on rather than hold it.
        if Task.isCancelled {
            release()
            throw CancellationError()
        }
    }

    /// Takes a cancelled waiter out of the queue; it never becomes the owner.
    private func dropWaiter(_ id: UUID) {
        guard let index = waiters.firstIndex(where: { $0.id == id }) else { return }
        waiters.remove(at: index).continuation.resume(returning: false)
    }

    private func release() {
        if waiters.isEmpty { busy = false } else { waiters.removeFirst().continuation.resume(returning: true) }
    }
}

/// Wraps MSAL for macOS behind `TokenProviding`. All interactive calls hop to the main actor.
final class MSALTokenProvider: TokenProviding, @unchecked Sendable {
    private let app: MSALPublicClientApplication
    private let anchor: AuthAnchorWindow
    private let gate = InteractiveGate()

    init(clientId: String, redirectUri: String, anchor: AuthAnchorWindow) throws {
        let authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/organizations")!)
        let config = MSALPublicClientApplicationConfig(clientId: clientId, redirectUri: redirectUri, authority: authority)
        app = try MSALPublicClientApplication(configuration: config)
        self.anchor = anchor
    }

    // MARK: TokenProviding

    func signIn(method: SignInMethod) async throws -> Identity {
        try await gate.run { [self] in
            let result = try await interactive(account: nil, tenantId: nil, scopes: [GraphScopes.userRead], claims: nil, prompt: .selectAccount)
            return Self.identity(from: result.account)
        }
    }

    func signOut(_ identity: Identity) async throws {
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
            guard let account = try? app.account(forIdentifier: identity.id) else { continue }
            try app.remove(account)
        }
    }

    func identities() async throws -> [Identity] {
        try app.allAccounts().map(Self.identity(from:))
    }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        guard let account = try? app.account(forIdentifier: identity.id) else { throw PIMError.interactionRequired }
        let params = MSALSilentTokenParameters(scopes: scopes, account: account)
        params.authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/\(tenantId)")!)
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

    static func identity(from account: MSALAccount) -> Identity {
        let claims = account.accountClaims ?? [:]
        return Identity(id: account.identifier ?? account.username ?? UUID().uuidString,
                        upn: account.username ?? "unknown",
                        displayName: (claims["name"] as? String) ?? account.username ?? "unknown",
                        homeTenantId: account.homeAccountId?.tenantId ?? (claims["tid"] as? String) ?? "",
                        signInMethod: .ownApp)
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
