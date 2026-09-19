import Testing
import Foundation
@testable import ElevateCore

@Suite struct StepUpTokenCacheTests {
    private func token(exp: Date?) -> String {
        var payload: [String: Any] = ["aud": "https://graph.microsoft.com", "acrs": "c10"]
        if let exp { payload["exp"] = Int(exp.timeIntervalSince1970) }
        let body = try! JSONSerialization.data(withJSONObject: payload)
        let b64 = body.base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
        return "eyJhbGciOiJub25lIn0.\(b64).sig"
    }

    private let scopes = ["https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/RoleManagementPolicy.Read.Directory"]

    @Test func handsBackTheStepUpTokenForTheSameIdentityTenantAndScopes() async {
        let cache = StepUpTokenCache()
        let stepped = token(exp: .now.addingTimeInterval(3600))
        await cache.store(stepped, identityId: "a", tenantId: "t", scopes: scopes)
        #expect(await cache.token(identityId: "a", tenantId: "t", scopes: scopes.reversed()) == stepped)
    }

    @Test func holdsNothingForAnotherIdentityTenantOrScopeSet() async {
        let cache = StepUpTokenCache()
        await cache.store(token(exp: .now.addingTimeInterval(3600)), identityId: "a", tenantId: "t", scopes: scopes)
        #expect(await cache.token(identityId: "b", tenantId: "t", scopes: scopes) == nil)
        #expect(await cache.token(identityId: "a", tenantId: "other", scopes: scopes) == nil)
        #expect(await cache.token(identityId: "a", tenantId: "t", scopes: ["https://management.azure.com/user_impersonation"]) == nil)
    }

    @Test func dropsTheTokenOnceItsOwnExpiryIsNear() async {
        let clock = Clock()
        let cache = StepUpTokenCache(now: { clock.now })
        await cache.store(token(exp: clock.now.addingTimeInterval(600)), identityId: "a", tenantId: "t", scopes: scopes)
        clock.advance(600 - StepUpTokenCache.skew + 1)
        #expect(await cache.token(identityId: "a", tenantId: "t", scopes: scopes) == nil)
    }

    @Test func anOpaqueTokenIsHeldOnlyLongEnoughForTheRetry() async {
        let clock = Clock()
        let cache = StepUpTokenCache(now: { clock.now })
        await cache.store("not-a-jwt", identityId: "a", tenantId: "t", scopes: scopes)
        #expect(await cache.token(identityId: "a", tenantId: "t", scopes: scopes) == "not-a-jwt")
        clock.advance(StepUpTokenCache.opaqueLifetime)
        #expect(await cache.token(identityId: "a", tenantId: "t", scopes: scopes) == nil)
    }

    @Test func signingOutForgetsWhatWasHeldForThatIdentityAlone() async {
        let cache = StepUpTokenCache()
        let kept = token(exp: .now.addingTimeInterval(3600))
        await cache.store(token(exp: .now.addingTimeInterval(3600)), identityId: "a", tenantId: "t", scopes: scopes)
        await cache.store(kept, identityId: "b", tenantId: "t", scopes: scopes)
        await cache.forget(identityId: "a")
        #expect(await cache.token(identityId: "a", tenantId: "t", scopes: scopes) == nil)
        #expect(await cache.token(identityId: "b", tenantId: "t", scopes: scopes) == kept)
    }

    private final class Clock: @unchecked Sendable {
        private let lock = NSLock()
        private var date = Date(timeIntervalSince1970: 1_700_000_000)
        var now: Date { lock.withLock { date } }
        func advance(_ seconds: TimeInterval) { lock.withLock { date += seconds } }
    }
}
