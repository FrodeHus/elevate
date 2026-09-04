import Foundation

struct AppConfig: Sendable {
    static let bundleId = "no.frodehus.pimtray"
    let clientId: String
    var redirectUri: String { "msauth.\(Self.bundleId)://auth" }

    enum ConfigError: LocalizedError {
        case missing, invalid
        var errorDescription: String? {
            switch self {
            case .missing: "PimTrayConfig.plist not found. Copy PimTrayConfig.plist.example to PimTrayConfig.plist, set ClientId, and rebuild."
            case .invalid: "PimTrayConfig.plist has no ClientId string."
            }
        }
    }

    static func load(bundle: Bundle = .main) throws -> AppConfig {
        guard let url = bundle.url(forResource: "PimTrayConfig", withExtension: "plist") else { throw ConfigError.missing }
        let data = try Data(contentsOf: url)
        guard let dict = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let clientId = dict["ClientId"] as? String, !clientId.isEmpty,
              clientId != "00000000-0000-0000-0000-000000000000" else { throw ConfigError.invalid }
        return AppConfig(clientId: clientId)
    }
}
