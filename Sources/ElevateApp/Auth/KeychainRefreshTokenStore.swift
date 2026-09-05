import Foundation
import Security
import ElevateCore

/// Refresh tokens for one client id, kept as generic-password items in the login keychain.
///
/// One item per identity, account `"<clientId>|<identityId>"` so two sign-in methods never
/// collide, and `AfterFirstUnlockThisDeviceOnly` so a background refresh works after a reboot
/// without the tokens ever leaving this Mac.
final class KeychainRefreshTokenStore: RefreshTokenStore {
    static let service = "no.frodehus.elevate.refresh"

    private let clientId: String

    init(clientId: String) { self.clientId = clientId }

    private var accountPrefix: String { "\(clientId)|" }
    private func account(for identityId: String) -> String { accountPrefix + identityId }

    private func baseQuery(account: String? = nil) -> [String: Any] {
        var q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecUseDataProtectionKeychain as String: true,
        ]
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
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
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
