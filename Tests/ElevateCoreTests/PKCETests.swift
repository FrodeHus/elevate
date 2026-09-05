import Testing
import Foundation
@testable import ElevateCore

@Suite struct PKCETests {
    @Test func challengeIsS256OfVerifier() {
        // RFC 7636 appendix B
        let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        #expect(PKCE.challenge(for: verifier) == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
    }

    @Test func generatedVerifierIsUrlSafeAndLongEnough() {
        let p = PKCE.generate()
        #expect(p.verifier.count >= 43 && p.verifier.count <= 128)
        #expect(p.verifier.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
        #expect(p.challenge == PKCE.challenge(for: p.verifier))
        #expect(PKCE.generate().verifier != p.verifier)
    }
}
