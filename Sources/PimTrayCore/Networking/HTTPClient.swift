import Foundation

public struct HTTPRequest: Sendable, Hashable {
    public var method: String
    public var url: URL
    public var headers: [String: String]
    public var body: Data?

    public init(method: String, url: URL, headers: [String: String] = [:], body: Data? = nil) {
        self.method = method
        self.url = url
        self.headers = headers
        self.body = body
    }
}

public struct HTTPResponse: Sendable {
    public let status: Int
    public let headers: [String: String]
    public let body: Data

    public init(status: Int, headers: [String: String] = [:], body: Data = Data()) {
        self.status = status
        self.headers = headers
        self.body = body
    }

    public func header(_ name: String) -> String? {
        headers.first { $0.key.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    public var bodyText: String { String(decoding: body, as: UTF8.self) }
}

public protocol HTTPClient: Sendable {
    func send(_ request: HTTPRequest) async throws -> HTTPResponse
}

public final class URLSessionHTTPClient: HTTPClient {
    private let session: URLSession
    public init(session: URLSession = .shared) { self.session = session }

    public func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        var req = URLRequest(url: request.url)
        req.httpMethod = request.method
        req.httpBody = request.body
        for (k, v) in request.headers { req.setValue(v, forHTTPHeaderField: k) }
        do {
            let (data, resp) = try await session.data(for: req)
            guard let http = resp as? HTTPURLResponse else { throw PIMError.network("non-HTTP response") }
            var headers: [String: String] = [:]
            for (k, v) in http.allHeaderFields { headers["\(k)"] = "\(v)" }
            return HTTPResponse(status: http.statusCode, headers: headers, body: data)
        } catch let e as PIMError {
            throw e
        } catch {
            throw PIMError.network(error.localizedDescription)
        }
    }
}
