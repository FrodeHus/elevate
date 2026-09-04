import Foundation

public enum GraphJSON {
    public static func parseDate(_ s: String) -> Date? {
        // Graph emits up to 7 fractional digits; ISO8601DateFormatter accepts at most 3, so trim.
        let trimmed = s.replacing(/\.(\d{3})\d+/) { "." + String($0.output.1) }
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

    public static var encoder: JSONEncoder {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(date.formatted(Date.ISO8601FormatStyle()))
        }
        return e
    }
}
