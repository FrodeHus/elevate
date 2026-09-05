import Foundation

public enum GraphJSON {
    public static func parseDate(_ s: String) -> Date? {
        // Graph emits 1-7 fractional digits; the ISO8601 style accepts exactly 3, so normalise.
        let trimmed = s.replacing(/\.(\d+)/) { match in
            "." + String(match.output.1.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
        }
        return (try? Date(trimmed, strategy: Date.ISO8601FormatStyle(includingFractionalSeconds: true)))
            ?? (try? Date(trimmed, strategy: Date.ISO8601FormatStyle()))
    }

    public static var decoder: JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .custom { decoder in
            let s = try decoder.singleValueContainer().decode(String.self)
            guard let date = parseDate(s) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Bad date \(s)"))
            }
            return date
        }
        return d
    }

    public static func encoderDateString(_ date: Date) -> String {
        date.formatted(Date.ISO8601FormatStyle())
    }

    public static var encoder: JSONEncoder {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(date.formatted(Date.ISO8601FormatStyle()))
        }
        return e
    }
}
