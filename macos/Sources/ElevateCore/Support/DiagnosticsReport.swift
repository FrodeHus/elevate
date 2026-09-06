import Foundation

/// One signed-in account, as shown in a diagnostics report. Never carries a
/// token, client id, or other secret — there is no field for one.
public struct DiagnosticsAccount: Sendable {
    public let upn: String
    public let method: String
    public let tenantCount: Int

    public init(upn: String, method: String, tenantCount: Int) {
        self.upn = upn
        self.method = method
        self.tenantCount = tenantCount
    }
}

/// One configured tenant, as shown in a diagnostics report.
public struct DiagnosticsTenant: Sendable {
    public let name: String
    public let id: String
    public let mode: String
    public let flags: [String]

    public init(name: String, id: String, mode: String, flags: [String]) {
        self.name = name
        self.id = id
        self.mode = mode
        self.flags = flags
    }
}

/// The input to `DiagnosticsReport.render`. Deliberately has no field for a
/// client id, token, or other secret, so none can appear in the rendered
/// text — callers must not pass secrets in `errors` either, since error
/// messages are rendered verbatim.
public struct DiagnosticsInput: Sendable {
    public let appVersion: String
    public let build: String
    public let signing: String
    public let os: String
    public let accounts: [DiagnosticsAccount]
    public let tenants: [DiagnosticsTenant]
    public let profiles: [String]
    public let hotKey: String?
    public let errors: [DiagnosticsError]

    public init(
        appVersion: String,
        build: String,
        signing: String,
        os: String,
        accounts: [DiagnosticsAccount],
        tenants: [DiagnosticsTenant],
        profiles: [String],
        hotKey: String?,
        errors: [DiagnosticsError]
    ) {
        self.appVersion = appVersion
        self.build = build
        self.signing = signing
        self.os = os
        self.accounts = accounts
        self.tenants = tenants
        self.profiles = profiles
        self.hotKey = hotKey
        self.errors = errors
    }
}

/// Renders a plain-text diagnostics report for "Copy diagnostics" in
/// Settings. Pure formatting: it does not filter or redact `errors` — the
/// caller is responsible for not passing secrets in error messages.
public enum DiagnosticsReport {
    public static func render(_ input: DiagnosticsInput, now: Date = .now) -> String {
        var lines: [String] = []

        lines.append("Elevate Diagnostics")
        lines.append("Generated: \(isoString(now))")
        lines.append("")
        lines.append("App version: \(input.appVersion) (\(input.build))")
        lines.append("Signing: \(input.signing)")
        lines.append("macOS: \(input.os)")
        lines.append("")

        lines.append("Accounts:")
        if input.accounts.isEmpty {
            lines.append("  None")
        } else {
            for account in input.accounts {
                lines.append("  \(account.upn) — \(account.method) — \(account.tenantCount) tenant(s)")
            }
        }
        lines.append("")

        lines.append("Tenants:")
        if input.tenants.isEmpty {
            lines.append("  None")
        } else {
            for tenant in input.tenants {
                let flags = tenant.flags.isEmpty ? "none" : tenant.flags.joined(separator: ", ")
                lines.append("  \(tenant.name) (\(tenant.id)) — mode: \(tenant.mode) — flags: \(flags)")
            }
        }
        lines.append("")

        lines.append("Profiles:")
        if input.profiles.isEmpty {
            lines.append("  None")
        } else {
            for profile in input.profiles {
                lines.append("  \(profile)")
            }
        }
        lines.append("")

        lines.append("Hot key: \(input.hotKey ?? "None")")
        lines.append("")

        lines.append("Recent errors:")
        if input.errors.isEmpty {
            lines.append("  None")
        } else {
            for error in input.errors {
                lines.append("  [\(isoString(error.date))] \(error.message)")
            }
        }

        return lines.joined(separator: "\n")
    }

    private static func isoString(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = TimeZone(identifier: "UTC")
        return formatter.string(from: date)
    }
}
