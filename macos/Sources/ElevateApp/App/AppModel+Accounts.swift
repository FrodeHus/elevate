import Foundation
import ElevateCore

@MainActor
extension AppModel {
    // MARK: Sign-in methods

    /// Fixed sign-in methods offered by "Add account…" (a custom client id is typed there).
    /// `.ownApp` is listed even when unconfigured; the view disables it and explains why.
    var availableMethods: [SignInMethod] { SignInMethod.builtIn }

    /// The custom client id used last time, for prefilling the add-account dialog.
    var rememberedCustomClientId: String { settings.customClientId }

    /// Whether a method can be used right now. A custom method needs a well-formed client id.
    ///
    /// `.ownApp` also needs a signature with entitlements: MSAL keeps its token cache in the
    /// shared data-protection keychain group, which an ad-hoc signed build cannot read
    /// (`errSecMissingEntitlement`, -34018), so sign-in would fail after the webview.
    func isAvailable(_ method: SignInMethod) -> Bool {
        switch method {
        case .ownApp: isConfigured && BuildInfo.signingState != .adHoc
        case .custom(let id): AppSettings.isValidClientId(id)
        default: method.clientId != nil
        }
    }

    // MARK: Accounts

    /// Signs in with `method` and adds the resulting account, its home tenant and its roles.
    /// Sets `notice` and leaves the state untouched when the sign-in fails. Returns whether an
    /// account was actually added (a saved-refresh-token warning still counts as success).
    @discardableResult
    func addAccount(method: SignInMethod = .ownApp) async -> Bool {
        guard isAvailable(method) else {
            switch method {
            case .ownApp: notice = "Complete initial setup first"
            case .custom: notice = "Enter the custom app's application (client) ID as a GUID"
            default: notice = "That sign-in method is unavailable"
            }
            logError("Add account (\(method.displayName)): \(notice ?? "unavailable")")
            return false
        }
        if case .custom(let id) = method { settings.customClientId = id }
        do {
            let identity = try await tokens.signIn(method: method)
            // The same account under a different method would fight over the same rows and tenants.
            if let existing = state.identities.first(where: { $0.id == identity.id }), existing.signInMethod != method {
                notice = "This account is already added with \(existing.signInMethod.displayName)"
                logError("Add account: already added with \(existing.signInMethod.displayName)")
                try? await tokens.signOut(identity)
                return false
            }
            if !state.identities.contains(where: { $0.id == identity.id }) {
                state.identities.append(identity)
            }
            if !method.usesMSAL, let failure = await loopback.provider(for: method)?.persistenceError() {
                notice = "Signed in, but the refresh token could not be saved to the Keychain: \(failure). You will be asked to sign in again after restart."
                logError("Refresh token not saved to the Keychain: \(failure)")
            }
            let homeKey = TenantKey(identityId: identity.id, tenantId: identity.homeTenantId)
            if tenant(homeKey) == nil {
                let name = (try? await discovery.tenantDisplayName(identity: identity, tenantId: identity.homeTenantId)) ?? identity.homeTenantId
                state.upsertTenant(TenantContext(identityId: identity.id, tenantId: identity.homeTenantId, displayName: name, source: .home))
            }
            persist()
            await refresh(homeKey)
            return true
        } catch {
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            notice = message
            logError("Add account (\(method.displayName)): \(message)")
            return false
        }
    }

    func signOut(_ identity: Identity) {
        Task {
            try? await tokens.signOut(identity)
            forgetIdentity(identity.id)
            persist()
        }
    }

    // MARK: Tenants

    func addTenant(identityId: String, domainOrId: String) async throws {
        let generation = configGeneration
        guard let identity = self.identity(identityId) else { throw PIMError.unexpected(status: 0, body: "Unknown identity") }
        let tenantId = try await discovery.resolveTenantId(domainOrId: domainOrId)
        guard generation == configGeneration else { return }
        let key = TenantKey(identityId: identityId, tenantId: tenantId)
        guard tenant(key) == nil else { return }
        let name = (try? await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: tenantId, scopes: [GraphScopes.userRead]) { @Sendable in
            try await discovery.tenantDisplayName(identity: identity, tenantId: tenantId)
        }) ?? domainOrId
        guard generation == configGeneration else { return }
        state.upsertTenant(TenantContext(identityId: identityId, tenantId: tenantId, displayName: name, source: .manual))
        persist()
        await refresh(key)
    }

    func discoverTenants(identityId: String) async throws -> [DiscoveredTenant] {
        guard let identity = self.identity(identityId) else { return [] }
        return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: identity.homeTenantId, scopes: ArmScopes.all) { @Sendable in
            try await discovery.discoverTenants(identity: identity)
        }
    }

    func trackTenants(identityId: String, tenants: [DiscoveredTenant]) async {
        let generation = configGeneration
        for t in tenants {
            let key = TenantKey(identityId: identityId, tenantId: t.tenantId)
            guard tenant(key) == nil else { continue }
            state.upsertTenant(TenantContext(identityId: identityId, tenantId: t.tenantId, displayName: t.displayName, source: .discovered))
        }
        persist()
        let keys = tenants.map { TenantKey(identityId: identityId, tenantId: $0.tenantId) }
        await withTaskGroup(of: Void.self) { group in
            for key in keys {
                group.addTask {
                    guard await self.configGeneration == generation else { return }
                    await self.refresh(key)
                }
            }
        }
    }

    func removeTenant(_ key: TenantKey) {
        declinedTenants.remove(key)
        state.removeTenant(key)
        roles[key] = nil
        active = active.filter { $0.key.tenantKey != key }
        dropApprovals { $0 == key }
        dropPolicies { $0.tenantKey == key }
        persist()
    }

    func retryDiscovery(_ key: TenantKey) async {
        declinedTenants.remove(key)
        guard var t = self.tenant(key) else { return }
        t.discoveryMode = .automatic
        t.lastDiscoveryError = nil
        t.azureUnavailableReason = nil
        t.groupsUnavailableReason = nil
        t.entraActivation = nil
        dropPolicies { $0.tenantKey == key }
        state.upsertTenant(t)
        persist()
        await refresh(key)
    }
}
