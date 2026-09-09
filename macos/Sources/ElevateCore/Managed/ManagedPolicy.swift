import Foundation

/// Pure decisions about what a managed configuration permits. No I/O, no state: the callers
/// (sign-in flows, account lists, diagnostics) ask these before offering or keeping an account.
public enum ManagedPolicy {
    /// Whether `method` may be used under `config`. With no `allowedSignInMethods` key in effect
    /// every method is allowed; otherwise the method's `kind` must be in the list, so `.custom`
    /// in the list permits any custom client id.
    public static func isAllowed(_ method: SignInMethod, by config: ManagedConfiguration) -> Bool {
        guard let allowed = config.allowedSignInMethods else { return true }
        return allowed.contains(method.kind)
    }

    /// Whether `tenantId` is in `allowedIds`, comparing case-insensitively. A nil list — the
    /// `AllowedTenants` key not in effect — allows every tenant.
    public static func isTenantAllowed(_ tenantId: String, allowedIds: Set<String>?) -> Bool {
        guard let allowedIds else { return true }
        return allowedIds.contains { $0.caseInsensitiveCompare(tenantId) == .orderedSame }
    }
}
