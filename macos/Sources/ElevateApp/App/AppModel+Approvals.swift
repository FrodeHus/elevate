import Foundation
import ElevateCore

@MainActor
extension AppModel {
    // MARK: Derived

    /// Every pending request across accounts and tenants, oldest first, tenant name and id as
    /// tiebreaks so the order is stable between refreshes. Filtered by the panel search when active.
    var approvalsOrdered: [ApprovalRequest] {
        let ordered = allApprovals.sorted { a, b in
            let da = a.createdAt ?? .distantPast, db = b.createdAt ?? .distantPast
            if da != db { return da < db }
            let na = approvalTenantName(a), nb = approvalTenantName(b)
            if na != nb { return na < nb }
            return a.id < b.id
        }
        guard isFiltering else { return ordered }
        return ordered.filter { r in
            [r.targetName, r.requesterName, approvalTenantName(r)].contains { PanelFilter.matches(query: searchQuery, text: $0) }
        }
    }

    /// The menu bar glyph's condition: everything pending, never narrowed by the panel search.
    var pendingApprovalCount: Int { allApprovals.count }

    private var allApprovals: [ApprovalRequest] { approvals.values.flatMap { $0.values.flatMap { $0 } } }

    /// Looks up a single request by id in the unfiltered set, so a request that falls outside the
    /// panel's current search still resolves for a sheet that is already showing it.
    func approval(id: String) -> ApprovalRequest? { allApprovals.first { $0.id == id } }

    func approvalTenantName(_ request: ApprovalRequest) -> String {
        tenant(request.tenantKey)?.displayName ?? request.tenantKey.tenantId
    }

    // MARK: Announcements

    /// Notifies once per request the user has not seen before. Only adds here: a single tenant's
    /// refresh knows nothing about the tenants that have not been read yet this launch, so pruning
    /// here would forget their ids and re-notify them a moment later. `refreshAll` prunes instead.
    // internal for AppModel+Refresh
    func announceNewApprovals() async {
        let all = allApprovals
        let newRequests = ApprovalDiff.newRequests(previousIds: settings.seenApprovalIds, current: all)
        // Mark every currently pending id as seen before the first await below, so a concurrently
        // refreshing tenant reading `settings.seenApprovalIds` cannot re-announce these same requests.
        settings.seenApprovalIds.formUnion(all.map(\.id))
        for r in newRequests {
            await notifier.notify(title: "Approval requested",
                                  body: "\(r.targetName) for \(r.requesterName), \(approvalTenantName(r))")
        }
    }

    /// After a full sweep every tenant's list is current, so the seen set can be cut back to what is
    /// still pending; a request that is withdrawn and comes back notifies again.
    // internal for AppModel+Refresh
    func pruneSeenApprovals() {
        settings.seenApprovalIds.formIntersection(allApprovals.map(\.id))
    }

    // MARK: Approvals

    /// Sends one Approve or Deny. Returns true when the service accepted it; the caller closes its
    /// sheet on true and shows `approvalErrors[request.id]` on false.
    func decide(_ request: ApprovalRequest, approve: Bool, justification: String) async -> Bool {
        guard !decisionInFlight.contains(request.id) else {
            approvalErrors[request.id] = "A decision for this request is already being sent."
            logError("Approval \(request.targetName): a decision is already being sent")
            return false
        }
        guard let identity = self.identity(request.tenantKey.identityId) else {
            approvalErrors[request.id] = "That account is no longer signed in."
            logError("Approval \(request.targetName): that account is no longer signed in")
            return false
        }
        guard let provider = approvalProviders[request.kind] else {
            approvalErrors[request.id] = "This request cannot be decided from Elevate."
            logError("Approval \(request.targetName): cannot be decided from Elevate")
            return false
        }
        let generation = configGeneration
        decisionInFlight.insert(request.id)
        approvalErrors[request.id] = nil
        defer { decisionInFlight.remove(request.id) }
        do {
            try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: request.tenantKey.tenantId,
                                           scopes: provider.scopes) { @Sendable in
                try await provider.decide(request, approve: approve, justification: justification, identity: identity)
            }
            guard generation == configGeneration else {
                approvalErrors[request.id] = "Decision not completed; refresh and try again"
                logError("Approval \(request.targetName): decision not completed")
                return false
            }
            // Drop the row now; the follow-up refresh re-lists it if a further approval stage remains.
            approvals[request.tenantKey]?[request.kind]?.removeAll { $0.id == request.id }
            approvalErrors[request.id] = nil
            settings.lastApprovalJustification = justification
            Task { await self.refresh(request.tenantKey, kinds: [request.kind]) }
            return true
        } catch {
            guard generation == configGeneration else {
                approvalErrors[request.id] = "Decision not completed; refresh and try again"
                logError("Approval \(request.targetName): decision not completed")
                return false
            }
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            approvalErrors[request.id] = message
            logError("Approval \(request.targetName): \(message)")
            return false
        }
    }
}
