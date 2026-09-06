import Foundation

/// A single recorded error, with its timestamp.
public struct DiagnosticsError: Sendable, Equatable {
    public let date: Date
    public let message: String

    public init(date: Date, message: String) {
        self.date = date
        self.message = message
    }
}

/// A ring buffer of the most recent errors, capped at a fixed capacity.
public struct ErrorLog: Sendable, Equatable {
    private var buffer: [DiagnosticsError] = []
    private let capacity: Int

    public init(capacity: Int = 50) {
        self.capacity = capacity
    }

    /// Appends an error, evicting the oldest entry once over capacity.
    public mutating func append(_ message: String, at date: Date = .now) {
        buffer.append(DiagnosticsError(date: date, message: message))
        if buffer.count > capacity {
            buffer.removeFirst(buffer.count - capacity)
        }
    }

    /// Entries ordered oldest to newest.
    public var entries: [DiagnosticsError] {
        buffer
    }
}
