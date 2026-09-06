import Foundation
import Security
import ElevateCore

/// Refresh tokens for one client id, kept as data-protection generic-password items in the
/// app's own keychain access group — or, on an ad-hoc signed build with no entitlements, as
/// plain generic-password items in the login keychain (see `baseQuery`).
///
/// One item per identity, account `"<clientId>|<identityId>"` so two sign-in methods never
/// collide, and `AfterFirstUnlockThisDeviceOnly` so a background refresh works after a reboot
/// without the tokens ever leaving this Mac. The access group is set explicitly so the items
/// land in Elevate's own group rather than whichever group the entitlement happens to list first
/// — except on an ad-hoc build, where `baseQuery` omits the access group entirely (see there).
final class KeychainRefreshTokenStore: RefreshTokenStore {
    static let service = "no.reothor.elevate.refresh"
    /// The access group actually used: the running app's own team-id prefix (read from its
    /// `application-identifier` entitlement) plus the bundle suffix, so a build signed by a
    /// different team still lands in its own group instead of failing every Keychain call.
    ///
    /// `applicationIdentifier` is nil only on an ad-hoc build (`BuildInfo.signingState ==
    /// .adHoc`), and `baseQuery` never adds this access group for that signing state — so the
    /// fallback below is never reached in practice. It is kept only as a defined value matching
    /// the first `keychain-access-groups` entry in `project.yml`
    /// (`$(AppIdentifierPrefix)no.reothor.elevate`, with the team id as the prefix), in case
    /// `baseQuery`'s signing-state check is ever removed or bypassed.
    static let accessGroup: String = {
        guard let identifier = BuildInfo.applicationIdentifier,
              let prefix = identifier.split(separator: ".", maxSplits: 1).first, !prefix.isEmpty
        else { return "VLJKN96D7N.no.reothor.elevate" }
        return "\(prefix).no.reothor.elevate"
    }()

    private let clientId: String

    init(clientId: String) { self.clientId = clientId }

    private var accountPrefix: String { "\(clientId)|" }
    private func account(for identityId: String) -> String { accountPrefix + identityId }

    /// The shared part of every query.
    ///
    /// An ad-hoc signed build has no `application-identifier` entitlement, so it belongs to no
    /// keychain access group and the data-protection keychain rejects all of its calls with
    /// `errSecMissingEntitlement` (-34018). Such a build therefore omits both keys and its items
    /// go to the legacy login keychain instead. Service and account names stay the same, so
    /// nothing else in the app has to care which keychain is in use.
    private func baseQuery(account: String? = nil) -> [String: Any] {
        var q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
        ]
        if BuildInfo.signingState != .adHoc {
            q[kSecAttrAccessGroup as String] = Self.accessGroup
            q[kSecUseDataProtectionKeychain as String] = true
        }
        if let account { q[kSecAttrAccount as String] = account }
        return q
    }

    func load(identityId: String) throws -> String? {
        var query = baseQuery(account: account(for: identityId))
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw Self.error(status) }
        guard let data = item as? Data else { return nil }
        return String(decoding: data, as: UTF8.self)
    }

    func save(_ token: String, identityId: String) throws {
        let query = baseQuery(account: account(for: identityId))
        let data = Data(token.utf8)
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let update = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw Self.error(update) }
        var add = query
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess else { throw Self.error(status) }
    }

    func delete(identityId: String) throws {
        let status = SecItemDelete(baseQuery(account: account(for: identityId)) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw Self.error(status) }
    }

    func allIdentityIds() throws -> [String] {
        var query = baseQuery()
        query[kSecReturnAttributes as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitAll
        var items: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &items)
        if status == errSecItemNotFound { return [] }
        guard status == errSecSuccess else { throw Self.error(status) }
        let attributes = (items as? [[String: Any]]) ?? []
        return attributes
            .compactMap { $0[kSecAttrAccount as String] as? String }
            .filter { $0.hasPrefix(accountPrefix) }
            .map { String($0.dropFirst(accountPrefix.count)) }
            .sorted()
    }

    private static func error(_ status: OSStatus) -> PIMError {
        .unexpected(status: Int(status), body: "Keychain error \(status)")
    }
}
