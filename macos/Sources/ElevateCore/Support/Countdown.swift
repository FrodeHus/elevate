import Foundation

public enum Countdown {
    /// Whole seconds until `end`, or nil once it has passed.
    public static func remaining(until end: Date, now: Date = .now) -> Duration? {
        let secs = end.timeIntervalSince(now)
        guard secs > 0 else { return nil }
        return .seconds(Int(secs.rounded(.down)))
    }

    /// A coarse "time until" label: "2 h 15 m", "15 m", or "now" under a minute (or once past).
    public static func until(_ date: Date, now: Date = .now) -> String {
        let minutes = Int(date.timeIntervalSince(now) / 60)
        guard minutes >= 1 else { return "now" }
        let (h, m) = (minutes / 60, minutes % 60)
        if h == 0 { return "\(m) m" }
        if m == 0 { return "\(h) h" }
        return "\(h) h \(m) m"
    }

    /// `HH:MM`, floored to the minute.
    public static func label(_ d: Duration) -> String {
        let total = Int(d.components.seconds)
        return String(format: "%02d:%02d", total / 3600, (total % 3600) / 60)
    }
}
