import Foundation
import CryptoKit

public struct PKCE: Sendable {
    public let verifier: String
    public let challenge: String

    public static func generate() -> PKCE {
        var g = SystemRandomNumberGenerator()
        let bytes = (0..<64).map { _ in UInt8.random(in: .min ... .max, using: &g) }
        let verifier = Data(bytes).base64URLEncodedString()
        return PKCE(verifier: verifier, challenge: challenge(for: verifier))
    }

    static func challenge(for verifier: String) -> String {
        Data(SHA256.hash(data: Data(verifier.utf8))).base64URLEncodedString()
    }
}

extension Data {
    func base64URLEncodedString() -> String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
    }
    init?(base64URLEncoded s: String) {
        var b = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b.count % 4 != 0 { b += "=" }
        self.init(base64Encoded: b)
    }
}
