import Foundation

public enum Countdown {
    /// Whole seconds until `end`, or nil once it has passed.
    public static func remaining(until end: Date, now: Date = .now) -> Duration? {
        let secs = end.timeIntervalSince(now)
        guard secs > 0 else { return nil }
        return .seconds(Int(secs.rounded(.down)))
    }

    /// A coarse "time until" label: "2 h 15 min", "15 min", or "now" under a minute (or once past).
    public static func until(_ date: Date, now: Date = .now) -> String {
        let minutes = Int(date.timeIntervalSince(now) / 60)
        guard minutes >= 1 else { return "now" }
        return units(minutes: minutes)
    }

    /// Hours and minutes in units ("2 h 41 min", "46 min", "1 h"), floored to the minute; under a
    /// minute is "< 1 min". Never `HH:MM`, which reads as a time of day next to a clock.
    public static func label(_ d: Duration) -> String {
        let minutes = Int(d.components.seconds) / 60
        guard minutes >= 1 else { return "< 1 min" }
        return units(minutes: minutes)
    }

    private static func units(minutes: Int) -> String {
        let (h, m) = (minutes / 60, minutes % 60)
        if h == 0 { return "\(m) min" }
        if m == 0 { return "\(h) h" }
        return "\(h) h \(m) min"
    }
}
