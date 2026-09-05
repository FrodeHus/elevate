import Foundation

/// The one-line body of the notification an activation run posts. Lives in Core so the wording is
/// testable without the app: the caller supplies the outcomes, how many requests were attempted and
/// a way to name a role, and gets back the sentence to show.
public enum ActivationSummary {
    /// - Parameters:
    ///   - outcomes: what the coordinator reported, possibly empty.
    ///   - attempted: how many requests the run set out to make. Zero means there was nothing to do;
    ///     a positive count with no outcomes means the run was abandoned or skipped every request.
    ///   - names: a display name for a role key, used only when several outcomes must be listed.
    public static func body(outcomes: [ActivationOutcome], attempted: Int,
                            names: (RoleKey) -> String, now: Date = .now) -> String {
        guard attempted > 0 else { return "Nothing to do" }
        guard !outcomes.isEmpty else { return "Not completed; open Elevate for details" }
        if outcomes.count == 1 { return single(outcomes[0], now: now) }
        return several(outcomes, names: names)
    }

    private static func single(_ outcome: ActivationOutcome, now: Date) -> String {
        switch outcome.result {
        case .activated(let a):
            guard let end = a.endDateTime else { return "Active" }
            let seconds = Int(end.timeIntervalSince(a.startDateTime))
            guard seconds > 0 else { return "Active" }
            return "Active for \(Countdown.label(.seconds(seconds)))"
        case .scheduled(let a):
            return "Scheduled to start in \(Countdown.until(a.startDateTime, now: now))"
        case .pendingApproval:
            return "Awaiting approval"
        case .failed(let e):
            return "Failed: \(e.userMessage)"
        }
    }

    private static func several(_ outcomes: [ActivationOutcome], names: (RoleKey) -> String) -> String {
        var ok = 0, scheduled = 0, pending = 0
        var failures: [String] = []
        for outcome in outcomes {
            switch outcome.result {
            case .activated: ok += 1
            case .scheduled: scheduled += 1
            case .pendingApproval: pending += 1
            // The outcome carries the key the provider resolved to, which is the one the app knows.
            case .failed(let e): failures.append("\(names(outcome.roleKey)): \(e.userMessage)")
            }
        }
        var parts: [String] = []
        // Only the first clause names the noun: "2 roles activated, 1 scheduled, 1 failed: …".
        func append(_ count: Int, _ suffix: String) {
            guard count > 0 else { return }
            parts.append(parts.isEmpty ? "\(roleCount(count)) \(suffix)" : "\(count) \(suffix)")
        }
        append(ok, "activated")
        append(scheduled, "scheduled")
        append(pending, "awaiting approval")
        if let first = failures.first {
            let more = failures.count - 1
            let detail = more > 0 ? "\(first) and \(more) more" : first
            append(failures.count, "failed: \(detail)")
        }
        return parts.isEmpty ? "Nothing to do" : parts.joined(separator: ", ")
    }

    /// "1 role" / "2 roles".
    private static func roleCount(_ n: Int) -> String { n == 1 ? "1 role" : "\(n) roles" }
}
