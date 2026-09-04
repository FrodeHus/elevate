import Foundation

public enum GraphJSON {
    nonisolated(unsafe) private static let fractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    nonisolated(unsafe) private static let plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    public static func parseDate(_ s: String) -> Date? {
        // Graph emits up to 7 fractional digits; ISO8601DateFormatter accepts at most 3, so trim.
        let trimmed = s.replacing(/\.(\d{3})\d+/) { "." + String($0.output.1) }
        return fractional.date(from: trimmed) ?? plain.date(from: trimmed)
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
            try c.encode(plain.string(from: date))
        }
        return e
    }
}
