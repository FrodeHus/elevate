import Foundation
import ElevateCore

/// Routes requests by HTTP method plus a substring of the URL. Records every request.
actor StubHTTPClient: HTTPClient {
    struct Route { let method: String; let urlContains: String; let respond: @Sendable (HTTPRequest) async throws -> HTTPResponse }
    private var routes: [Route] = []
    private(set) var requests: [HTTPRequest] = []

    func on(_ method: String, _ urlContains: String, status: Int = 200, headers: [String: String] = [:], body: Data = Data()) {
        routes.append(Route(method: method, urlContains: urlContains) { _ in HTTPResponse(status: status, headers: headers, body: body) })
    }

    func on(_ method: String, _ urlContains: String, respond: @escaping @Sendable (HTTPRequest) -> HTTPResponse) {
        routes.append(Route(method: method, urlContains: urlContains, respond: respond))
    }

    /// A route that never answers: the request ends only when the caller's own deadline cancels
    /// it. Used to prove a bounded fetch is bounded.
    func hang(_ method: String, _ urlContains: String) {
        routes.append(Route(method: method, urlContains: urlContains) { _ in
            try await Task.sleep(for: .seconds(600))
            return HTTPResponse(status: 599)
        })
    }

    func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        requests.append(request)
        guard let route = routes.last(where: { $0.method == request.method && request.url.absoluteString.contains($0.urlContains) }) else {
            return HTTPResponse(status: 599, body: Data("no stub for \(request.method) \(request.url)".utf8))
        }
        return try await route.respond(request)
    }

    func requests(matching substring: String) -> [HTTPRequest] {
        requests.filter { $0.url.absoluteString.contains(substring) }
    }
}
