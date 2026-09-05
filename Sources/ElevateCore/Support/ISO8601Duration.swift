import Foundation

public enum ISO8601Duration {
    /// Parses `PnDTnHnMnS` (days, hours, minutes, seconds). Weeks, months, years are not supported by Graph PIM.
    public static func parse(_ text: String) -> Duration? {
        let pattern = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$/
        guard let m = text.wholeMatch(of: pattern) else { return nil }
        let days = Int(m.1 ?? "0") ?? 0
        let hours = Int(m.2 ?? "0") ?? 0
        let minutes = Int(m.3 ?? "0") ?? 0
        let seconds = Double(m.4 ?? "0") ?? 0
        let total = Double(days * 86400 + hours * 3600 + minutes * 60) + seconds
        guard total > 0 || text == "PT0S" else { return nil }
        return .seconds(total)
    }

    /// Formats whole hours and minutes, e.g. `PT1H30M`. Seconds are dropped.
    public static func format(_ duration: Duration) -> String {
        let totalMinutes = Int(duration.components.seconds / 60)
        let h = totalMinutes / 60, m = totalMinutes % 60
        var out = "PT"
        if h > 0 { out += "\(h)H" }
        if m > 0 || h == 0 { out += "\(m)M" }
        return out
    }
}
