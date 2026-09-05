import Foundation

public struct ActivationOutcome: Hashable, Sendable {
    public enum Result: Hashable, Sendable {
        case activated(ActiveAssignment)
        case pendingApproval(ActiveAssignment)
        case scheduled(ActiveAssignment)
        case failed(PIMError)
    }
    public let roleKey: RoleKey
    public let result: Result
    public init(roleKey: RoleKey, result: Result) { self.roleKey = roleKey; self.result = result }
}

/// Runs activations grouped by identity+tenant: groups in parallel, requests within a group in sequence.
/// Handles `interactionRequired` and `claimsChallenge` with one interactive prompt and one retry per request.
public final class ActivationCoordinator: Sendable {
    private let providers: [RoleScopeKind: any PIMProvider]
    private let tokens: any TokenProviding

    public init(providers: [any PIMProvider], tokens: any TokenProviding) {
        self.providers = Dictionary(uniqueKeysWithValues: providers.map { ($0.kind, $0) })
        self.tokens = tokens
    }

    public func provider(for kind: RoleScopeKind) -> (any PIMProvider)? { providers[kind] }

    public func activate(_ requests: [ActivationRequest], identities: [Identity],
                         onProgress: @Sendable @escaping (ActivationOutcome) -> Void) async -> [ActivationOutcome] {
        let identityById = Dictionary(uniqueKeysWithValues: identities.map { ($0.id, $0) })
        let groups = Dictionary(grouping: requests) { $0.roleKey.tenantKey }
        return await withTaskGroup(of: [ActivationOutcome].self) { group in
            for (tenantKey, groupRequests) in groups {
                group.addTask { [self] in
                    var outcomes: [ActivationOutcome] = []
                    for request in groupRequests {
                        let outcome = await self.activateOne(request, identity: identityById[tenantKey.identityId])
                        onProgress(outcome)
                        outcomes.append(outcome)
                    }
                    return outcomes
                }
            }
            var all: [ActivationOutcome] = []
            for await batch in group { all += batch }
            return all
        }
    }

    private func activateOne(_ request: ActivationRequest, identity: Identity?) async -> ActivationOutcome {
        guard let identity else {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(.unexpected(status: 0, body: "Unknown identity \(request.roleKey.identityId)")))
        }
        guard let provider = providers[request.roleKey.scope.kind] else {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(.unexpected(status: 501, body: "No provider for \(request.roleKey.scope.kind)")))
        }
        // A role can demand a fresh MFA or a Conditional Access authentication context. The
        // service answers with a 400 and no claims header, so we choose the claims: the policy's
        // authentication context when it has one, otherwise a plain MFA re-verification.
        let fallbackClaims = request.authenticationContext.map(ClaimsChallenge.authenticationContext) ?? ClaimsChallenge.multiFactor
        do {
            let assignment = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: request.roleKey.tenantId,
                                                            scopes: provider.scopes, fallbackClaims: fallbackClaims) {
                try await provider.activate(request, identity: identity)
            }
            switch assignment.status {
            case .pendingApproval: return ActivationOutcome(roleKey: request.roleKey, result: .pendingApproval(assignment))
            case .scheduled: return ActivationOutcome(roleKey: request.roleKey, result: .scheduled(assignment))
            case .failed(let m): return ActivationOutcome(roleKey: request.roleKey, result: .failed(.unexpected(status: 0, body: m)))
            default: return ActivationOutcome(roleKey: request.roleKey, result: .activated(assignment))
            }
        } catch PIMError.interactionRequired, PIMError.claimsChallenge {
            // The retry already sent the user through the browser once; a second refusal means
            // that sign-in did not satisfy the role's requirement.
            let requirement = request.authenticationContext.map { "the Conditional Access authentication context \"\($0)\"" } ?? "multi-factor authentication"
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(.policyViolation(
                "This role requires \(requirement) and the sign-in did not satisfy it. Try again and complete the verification in the browser.")))
        } catch let e as PIMError {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(e))
        } catch {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(.network(error.localizedDescription)))
        }
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let provider = providers[assignment.roleKey.scope.kind] else {
            throw PIMError.unexpected(status: 501, body: "No provider for \(assignment.roleKey.scope.kind)")
        }
        try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: assignment.roleKey.tenantId, scopes: provider.scopes) {
            try await provider.deactivate(assignment, identity: identity)
        }
    }

    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let provider = providers[assignment.roleKey.scope.kind] else {
            throw PIMError.unexpected(status: 501, body: "No provider for \(assignment.roleKey.scope.kind)")
        }
        try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: assignment.roleKey.tenantId, scopes: provider.scopes) {
            try await provider.cancelPendingRequest(assignment, identity: identity)
        }
    }
}
