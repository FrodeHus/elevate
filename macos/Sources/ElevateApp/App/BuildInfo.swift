import Foundation
import Security

/// Facts about the running binary: its version numbers and how it was signed.
///
/// The signing state decides two things at runtime — which keychain the refresh tokens go to
/// (an ad-hoc build has no entitlements, so it cannot use an access group) and what the
/// diagnostics report says. Everything here is read once, on first use: the Security calls are
/// synchronous, thread-safe and answer about this process, so the answer never changes.
enum BuildInfo {
    /// How this copy of the app was signed.
    enum SigningState: String, Sendable {
        /// Signed with a Developer ID Application certificate (a distributable release).
        case developerID
        /// Signed for development: it has an `application-identifier` entitlement but no
        /// Developer ID leaf certificate.
        case development
        /// Ad-hoc signed (or unsigned): no `application-identifier` entitlement at all.
        case adHoc
    }

    /// `CFBundleShortVersionString`, e.g. "1.0.0".
    nonisolated static let version: String =
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"

    /// `CFBundleVersion`, e.g. "1".
    nonisolated static let build: String =
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0"

    /// The running process's `application-identifier` entitlement ("<team id>.<bundle id>"),
    /// or nil on an ad-hoc signed build, which carries no entitlements at all.
    ///
    /// macOS spells the entitlement `com.apple.application-identifier`; the unprefixed iOS
    /// spelling is checked too so the same code works wherever it is read.
    nonisolated static let applicationIdentifier: String? = {
        guard let task = SecTaskCreateFromSelf(nil) else { return nil }
        for key in ["com.apple.application-identifier", "application-identifier"] {
            guard let value = SecTaskCopyValueForEntitlement(task, key as CFString, nil),
                  let identifier = value as? String, !identifier.isEmpty else { continue }
            return identifier
        }
        return nil
    }()

    /// How this copy was signed, computed once.
    nonisolated static let signingState: SigningState = {
        guard applicationIdentifier != nil else { return .adHoc }
        return hasDeveloperIDLeaf() ? .developerID : .development
    }()

    /// A short phrase for the diagnostics report and the settings version line.
    nonisolated static var signingDescription: String {
        switch signingState {
        case .developerID: "Developer ID"
        case .development: "development"
        case .adHoc: "ad-hoc (unsigned)"
        }
    }

    /// True when the leaf certificate of this process's signature is a Developer ID Application
    /// certificate. `codesign` is not available at runtime, so read the signing information
    /// straight from the static code object.
    private static func hasDeveloperIDLeaf() -> Bool {
        var code: SecCode?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code else { return false }
        var info: CFDictionary?
        let flags = SecCSFlags(rawValue: kSecCSSigningInformation)
        guard SecCodeCopySigningInformation(unsafeBitCast(code, to: SecStaticCode.self), flags, &info) == errSecSuccess,
              let dictionary = info as? [String: Any],
              let certificates = dictionary[kSecCodeInfoCertificates as String] as? [SecCertificate],
              let leaf = certificates.first,
              let summary = SecCertificateCopySubjectSummary(leaf) as String?
        else { return false }
        return summary.hasPrefix("Developer ID Application")
    }
}
