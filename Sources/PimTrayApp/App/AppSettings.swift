import Foundation
import Observation

/// User-editable configuration. The client id is the only required value; it lives in UserDefaults, not in a bundled plist.
@MainActor
@Observable
final class AppSettings {
    static let bundleId = "no.frodehus.pimtray"
    static var redirectUri: String { "msauth.\(bundleId)://auth" }
    static let clientIdKey = "clientId"

    private let defaults: UserDefaults

    var clientId: String {
        didSet { defaults.set(clientId, forKey: Self.clientIdKey) }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        clientId = defaults.string(forKey: Self.clientIdKey) ?? ""
    }

    var isConfigured: Bool { Self.isValidClientId(clientId) }

    static func isValidClientId(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return UUID(uuidString: trimmed) != nil && trimmed != "00000000-0000-0000-0000-000000000000"
    }
}
