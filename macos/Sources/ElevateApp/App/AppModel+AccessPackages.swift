import Foundation
import ElevateCore

@MainActor
extension AppModel {
    static let accessPackagePanelThrottle: TimeInterval = 15 * 60
    static let accessPackageBackgroundInterval: TimeInterval = 8 * 3600

    // MARK: Reads

    func accessPackageSnapshot(_ key: TenantKey) -> AccessPackageSnapshot? { state.accessPackageRecord(key)?.snapshot }
    func accessPackagesPolledAt(_ key: TenantKey) -> Date? { state.accessPackageRecord(key)?.polledAt }

    /// Tenants whose token carries the entitlement scope.
    private var accessPackageTenants: [TenantContext] { state.tenants.filter { $0.accessPackagesAvailable == true } }

    // MARK: Polling

    /// Polls every eligible tenant that has not been polled within the throttle window.
    func pollAccessPackagesIfDue(force: Bool = false) async {
        guard bootstrapped, isOnline else { return }
        let due = accessPackageTenants.filter { tenant in
            guard !force, let at = accessPackagesPolledAt(tenant.id) else { return true }
            return Date().timeIntervalSince(at) > Self.accessPackagePanelThrottle
        }
        await withTaskGroup(of: Void.self) { group in
            // Background polling: never prompt for a sign-in from a timer or a panel open.
            for tenant in due { group.addTask { await self.pollAccessPackages(tenant.id, interactive: false) } }
        }
    }

    /// Reads requests and assignments for one tenant, notifies about what changed, persists.
    /// `interactive` allows a sign-in prompt when silent acquisition fails; the default suits the
    /// access package window, which the user opened. Background polls pass false.
    func pollAccessPackages(_ key: TenantKey, interactive: Bool = true) async {
        guard let identity = identity(key.identityId), let tenant = tenant(key), !accessPackagesPolling.contains(key) else { return }
        guard !signInNeeded.contains(identity.id), !declinedTenants.contains(key) else { return }
        let generation = configGeneration
        accessPackagesPolling.insert(key)
        defer { accessPackagesPolling.remove(key) }
        let provider = accessPackageProvider
        let tokens = tokens
        func acquire<T: Sendable>(_ op: @Sendable @escaping () async throws -> T) async throws -> T {
            guard interactive else { return try await op() }
            return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes, operation: op)
        }
        do {
            let requests = try await acquire { @Sendable in
                try await provider.myRequests(identity: identity, tenantId: key.tenantId)
            }
            let assignments = try await acquire { @Sendable in
                try await provider.myAssignments(identity: identity, tenantId: key.tenantId)
            }
            guard generation == configGeneration, self.tenant(key) != nil else { return }
            let current = AccessPackageSnapshot(requests: requests, assignments: assignments)
            let previous = accessPackageSnapshot(key)
            let events = AccessPackageDiff.events(previous: previous, current: current, now: .now)
            state.setAccessPackages(key, snapshot: current, polledAt: .now)
            accessPackageErrors[key] = nil
            persist()
            for event in events { await notify(event, tenant: tenant) }
            await reschedulePackageExpiries()
        } catch is CancellationError {
            return
        } catch {
            guard generation == configGeneration else { return }
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            accessPackageErrors[key] = message
            logError("Access packages in \(tenant.displayName): \(message)")
        }
    }

    private func notify(_ event: AccessPackageEvent, tenant: TenantContext) async {
        let where_ = "\(event.packageName) in \(tenant.displayName)"
        switch event {
        case .approved: await notifier.notify(title: "Access package approved", body: where_)
        case .denied: await notifier.notify(title: "Access package denied", body: where_)
        case .deliveryFailed: await notifier.notify(title: "Access package delivery failed", body: where_)
        case .revoked: await notifier.notify(title: "Access package revoked", body: where_)
        // An assignment with a known end already has a timed toast from the notifier; a second one
        // from the poll would repeat it. Only an end the service never told us about needs this.
        case .expired(let a) where a.expiresAt == nil: await notifier.notify(title: "Access package expired", body: where_)
        case .expired: break
        }
    }

    /// Waits out a poll for this tenant already in flight, then starts a fresh one. `requestPackage`
    /// and `cancelPackageRequest` need this rather than calling `pollAccessPackages` directly: that
    /// method returns immediately when a poll for the tenant is already running, so a request made
    /// while one is in flight would otherwise never land in the snapshot.
    private func waitForPollThenPoll(_ key: TenantKey) async {
        var iterations = 0
        while accessPackagesPolling.contains(key), iterations < 50 {
            try? await Task.sleep(for: .milliseconds(100))
            iterations += 1
        }
        await pollAccessPackages(key)
    }

    /// Every delivered assignment with an end date, across tenants, handed to the notifier so the
    /// expiry notification fires on time even between polls.
    // internal for tests
    func reschedulePackageExpiries() async {
        var expiries: [PackageExpiry] = []
        for record in state.accessPackages {
            let tenantName = tenant(record.tenantKey)?.displayName ?? record.tenantKey.tenantId
            for a in record.snapshot.assignments where a.state == .delivered {
                guard let end = a.expiresAt else { continue }
                expiries.append(PackageExpiry(id: a.id, packageName: a.packageName, tenantName: tenantName, at: end))
            }
        }
        await notifier.setPackageExpiries(expiries)
    }

    // MARK: Window data

    func requestablePackages(_ key: TenantKey) async throws -> [AccessPackage] {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.requestablePackages(identity: identity, tenantId: key.tenantId)
        }
    }

    func packageRequirements(_ key: TenantKey, packageId: String) async throws -> [PolicyRequirement] {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.requirements(packageId: packageId, identity: identity, tenantId: key.tenantId)
        }
    }

    /// Submits the request, then re-polls so the Requested tab shows it.
    func requestPackage(_ key: TenantKey, packageId: String, policyId: String?, justification: String) async throws {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        _ = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.request(packageId: packageId, policyId: policyId, justification: justification, identity: identity, tenantId: key.tenantId)
        }
        await waitForPollThenPoll(key)
    }

    func cancelPackageRequest(_ key: TenantKey, requestId: String) async throws {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.cancel(requestId: requestId, identity: identity, tenantId: key.tenantId)
        }
        await waitForPollThenPoll(key)
    }
}
