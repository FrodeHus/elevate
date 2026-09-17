import Foundation
import ElevateCore

@MainActor
extension AppModel {
    // MARK: Sign-in methods

    /// Fixed sign-in methods offered by "Add account…" (a custom client id is typed there).
    /// `.ownApp` is listed even when unconfigured; the view disables it and explains why. A method
    /// the organization does not permit is not listed at all.
    var availableMethods: [SignInMethod] { SignInMethod.builtIn.filter { isMethodAllowed($0) } }

    /// Whether the "Company app (client ID)" row is offered; the client id typed into it does not change the
    /// answer, since the managed allow-list names kinds of method, not registrations.
    var isCustomMethodAllowed: Bool { isMethodAllowed(.custom(clientId: "")) }

    /// The custom client id used last time, for prefilling the add-account dialog.
    var rememberedCustomClientId: String { settings.customClientId }

    /// The last client id typed into "Use a different registration", for prefilling Add account.
    var rememberedPinnedClientId: String { settings.pinnedClientId }

    /// Whether an account can use an Entra app registration of its own: the organization allows
    /// the method and has not fixed the client id, and this build has a way to sign in with it.
    var canPin: Bool {
        isMethodAllowed(.ownApp) && !settings.isClientIdManaged && (pinnedMSAL != nil || ownAppViaLoopback)
    }

    /// How Add account and the Change app registration sheet name the Settings registration.
    var settingsRegistrationLabel: String {
        if settings.isClientIdManaged { return "managed by your organization" }
        if usesSharedApp { return "shared Elevate app" }
        guard let id = effectiveClientId(for: .ownApp) else { return "not configured" }
        return "\(id.prefix(8))…"
    }

    func matchesSettingsClientId(_ raw: String) -> Bool {
        guard let id = effectiveClientId(for: .ownApp) else { return false }
        return SignInMethod.normalizedClientId(id) == SignInMethod.normalizedClientId(raw)
    }

    /// Whether any request for the account is running, so its registration must not change now.
    func isAccountBusy(_ identityId: String) -> Bool {
        inFlight.contains { $0.identityId == identityId } || busy.contains { $0.identityId == identityId }
    }

    /// The loopback provider holding `method`'s refresh tokens, or nil when MSAL holds them.
    func loopbackStore(for method: SignInMethod) -> LoopbackTokenProvider? {
        switch method {
        case .ownApp: ownAppLoopbackProvider
        case .pinnedApp(let id): ownAppViaLoopback ? loopback.provider(clientId: id, reportedMethod: method) : nil
        default: loopback.provider(for: method)
        }
    }

    /// Whether a method can be used right now. A custom method needs a well-formed client id.
    ///
    /// `.ownApp` needs a client id and a transport for it: MSAL on a signed build, or — when MSAL
    /// is unusable because the build is ad-hoc signed and cannot read its shared data-protection
    /// keychain group (`errSecMissingEntitlement`, -34018) — the loopback flow over the same
    /// client id. `isConfigured` already covers both.
    func isAvailable(_ method: SignInMethod) -> Bool {
        guard isMethodAllowed(method) else { return false }
        return switch method {
        case .ownApp: isConfigured
        case .pinnedApp(let id): canPin && AppSettings.isValidClientId(id)
        case .custom(let id): AppSettings.isValidClientId(id)
        default: method.clientId != nil
        }
    }

    /// Names the token store `method`'s refresh token would be kept in: a loopback keychain item,
    /// keyed by client id, or an MSAL account, keyed by its normalised client id. Two methods that
    /// return the same key share one cache slot and one refresh token; nil means `method` has no
    /// usable store at all (an Entra app registration form on a signed build with no effective
    /// client id).
    private func tokenStoreKey(for method: SignInMethod) -> String? {
        if let id = loopbackClientId(for: method) { return "loopback:\(id)" }
        guard method.usesMSAL, !ownAppViaLoopback, let id = effectiveClientId(for: method) else { return nil }
        return "msal:\(SignInMethod.normalizedClientId(id))"
    }

    /// The client id `method`'s loopback keychain store uses, or nil when it keeps its tokens in
    /// MSAL's cache instead (either Entra app registration form on a signed build).
    private func loopbackClientId(for method: SignInMethod) -> String? {
        guard method.usesMSAL else { return method.clientId }
        guard ownAppViaLoopback else { return nil }
        return effectiveClientId(for: method)
    }

    // MARK: Accounts

    /// Signs in with `method` and adds the resulting account, its home tenant and its roles.
    /// Sets `notice` and leaves the state untouched when the sign-in fails. Returns whether an
    /// account was actually added (a saved-refresh-token warning still counts as success).
    @discardableResult
    func addAccount(method: SignInMethod = .ownApp) async -> Bool {
        guard isMethodAllowed(method) else {
            notice = Self.disallowedMethodNotice
            logError("Add account (\(method.displayName)): \(Self.disallowedMethodNotice)")
            return false
        }
        guard isAvailable(method) else {
            switch method {
            case .ownApp: notice = "Complete initial setup first"
            case .pinnedApp: notice = canPin ? "Enter the registration's application (client) ID as a GUID" : "Your own app registration is unavailable in this build"
            case .custom: notice = "Enter the custom app's application (client) ID as a GUID"
            default: notice = "That sign-in method is unavailable"
            }
            logError("Add account (\(method.displayName)): \(notice ?? "unavailable")")
            return false
        }
        if case .custom(let id) = method { settings.customClientId = id }
        if case .pinnedApp(let id) = method { settings.pinnedClientId = id }
        do {
            let identity = try await tokens.signIn(method: method)
            // The same account under a different method would fight over the same rows and tenants.
            if let existing = state.identities.first(where: { $0.id == identity.id }), existing.signInMethod != method {
                notice = "This account is already added with \(existing.signInMethod.detailedName)"
                logError("Add account: already added with \(existing.signInMethod.detailedName)")
                // Discard the sign-in we just made, but only when it does not share a token store
                // with the account that is already there: refresh tokens are keyed
                // "<clientId>|<identityId>", so on an unsigned build the `.ownApp` stand-in and a
                // `.custom` account over the same Settings client id are the *same* keychain item,
                // and a pinned id equal to the Settings id is the same MSAL account on a signed
                // build — either way, signing out would delete the existing account's token.
                let added = tokenStoreKey(for: method)
                if added == nil || added != tokenStoreKey(for: existing.signInMethod) {
                    try? await tokens.signOut(identity)
                }
                return false
            }
            if !state.identities.contains(where: { $0.id == identity.id }) {
                state.identities.append(identity)
            }
            // The own-app method keeps its refresh token in the Keychain too when it runs through
            // the loopback flow, so its save failures must be surfaced the same way.
            let store = loopbackStore(for: method)
            if let failure = await store?.persistenceError() {
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
            await trackPinnedTenants(identityId: identity.id)
            return true
        } catch {
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            notice = message
            logError("Add account (\(method.displayName)): \(message)")
            return false
        }
    }

    /// Whether `identityId` is kept in the list without a usable saved sign-in.
    func needsSignIn(_ identityId: String) -> Bool { signInNeeded.contains(identityId) }

    /// Signs an account marked `signInNeeded` in again with the method it was added with, keeping
    /// its tenants and configured roles. Sets `notice` and keeps the flag when the sign-in fails or
    /// the browser comes back with a different account. Returns whether the account is usable again.
    @discardableResult
    func retrySignIn(_ identity: Identity) async -> Bool {
        let method = identity.signInMethod
        guard isMethodAllowed(method) else {
            notice = Self.disallowedMethodNotice
            logError("Sign in again (\(method.displayName)): \(Self.disallowedMethodNotice)")
            return false
        }
        guard isAvailable(method) else {
            notice = method == .ownApp ? "Complete initial setup first" : "That sign-in method is unavailable"
            logError("Sign in again (\(method.displayName)): \(notice ?? "unavailable")")
            return false
        }
        do {
            let signedIn = try await tokens.signIn(method: method)
            guard signedIn.id == identity.id else {
                // A different account came back. Its token is keyed by its own id, so discarding it
                // cannot touch the one we were waiting for.
                try? await tokens.signOut(signedIn)
                notice = "Signed in as \(signedIn.upn), but \(identity.upn) was expected. Sign out \(identity.upn) if you no longer need it."
                logError("Sign in again: got \(signedIn.upn), expected \(identity.upn)")
                return false
            }
            signInNeeded.remove(identity.id)
            let store = loopbackStore(for: method)
            if let failure = await store?.persistenceError() {
                notice = "Signed in, but the refresh token could not be saved to the Keychain: \(failure). You will be asked to sign in again after restart."
                logError("Refresh token not saved to the Keychain: \(failure)")
            } else {
                notice = nil
            }
            let keys = tenants(for: identity.id).map(\.id)
            for key in keys { tenantErrors[key] = nil }
            let generation = configGeneration
            await withTaskGroup(of: Void.self) { group in
                for key in keys {
                    group.addTask {
                        guard await self.configGeneration == generation else { return }
                        await self.refresh(key)
                    }
                }
            }
            return true
        } catch {
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            notice = message
            logError("Sign in again (\(method.displayName)): \(message)")
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
        guard isTenantAllowed(tenantId) else {
            throw PIMError.unexpected(status: 0, body: Self.disallowedTenantMessage(domainOrId))
        }
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
        let tenants = tenants.filter { isTenantAllowed($0.tenantId) }
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
        guard !isPinnedTenant(key) else {
            notice = Self.pinnedTenantNotice
            return
        }
        forgetTenant(key)
        persist()
    }

    /// Drops one tenant and everything derived from it, without saving: the callers that remove
    /// several at once persist the result themselves.
    // internal for AppModel+Managed
    func forgetTenant(_ key: TenantKey) {
        declinedTenants.remove(key)
        state.removeTenant(key)
        roles[key] = nil
        active = active.filter { $0.key.tenantKey != key }
        dropApprovals { $0 == key }
        dropPolicies { $0.tenantKey == key }
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
