import Foundation

public enum Countdown {
    /// Whole seconds until `end`, or nil once it has passed.
    public static func remaining(until end: Date, now: Date = .now) -> Duration? {
        let secs = end.timeIntervalSince(now)
        guard secs > 0 else { return nil }
        return .seconds(Int(secs.rounded(.down)))
    }

    /// `HH:MM`, floored to the minute.
    public static func label(_ d: Duration) -> String {
        let total = Int(d.components.seconds)
        return String(format: "%02d:%02d", total / 3600, (total % 3600) / 60)
    }
}
