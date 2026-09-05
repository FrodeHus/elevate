import Foundation
import Network
import ElevateCore

/// A one-shot HTTP listener on an ephemeral loopback port, used as the OAuth redirect target.
///
/// Microsoft's identity platform treats `http://localhost:<port>` as a valid public-client
/// redirect without registering the port, which is what lets the first-party client ids work
/// with no app registration of our own.
actor LoopbackListener {
    private let listener: NWListener
    private let sink: ConnectionSink
    private let queue = DispatchQueue(label: "no.frodehus.elevate.loopback")
    /// The port the browser will be redirected to.
    nonisolated let port: UInt16

    nonisolated var redirectURI: String { "http://localhost:\(port)" }

    private init(listener: NWListener, sink: ConnectionSink, port: UInt16) {
        self.listener = listener
        self.sink = sink
        self.port = port
    }

    /// Binds a listener on a free loopback port and returns once it is ready to accept.
    static func start() async throws -> LoopbackListener {
        let parameters = NWParameters.tcp
        parameters.requiredInterfaceType = .loopback
        parameters.acceptLocalOnly = true
        parameters.allowLocalEndpointReuse = true
        let listener: NWListener
        do {
            listener = try NWListener(using: parameters, on: .any)
        } catch {
            throw PIMError.network("Could not open a local port for sign-in: \(error.localizedDescription)")
        }
        let queue = DispatchQueue(label: "no.frodehus.elevate.loopback.start")
        let waiter = OneShot<UInt16>()
        // NWListener fails to bind unless a connection handler is set before `start`, and the
        // browser can arrive before `waitForCode` runs, so the sink buffers until then.
        let sink = ConnectionSink()
        listener.newConnectionHandler = { sink.accept($0) }
        listener.stateUpdateHandler = { state in
            switch state {
            case .ready:
                if let port = listener.port?.rawValue {
                    waiter.finish(.success(port))
                } else {
                    waiter.finish(.failure(PIMError.network("Local sign-in port was not assigned")))
                }
            case .failed(let error):
                waiter.finish(.failure(PIMError.network("Local sign-in listener failed: \(error.localizedDescription)")))
            case .cancelled:
                waiter.finish(.failure(PIMError.network("Local sign-in listener was cancelled")))
            default:
                break
            }
        }
        listener.start(queue: queue)
        do {
            let port = try await waiter.value()
            listener.stateUpdateHandler = nil
            return LoopbackListener(listener: listener, sink: sink, port: port)
        } catch {
            listener.cancel()
            throw error
        }
    }

    /// Waits for the browser's redirect and returns the authorization code.
    ///
    /// Stops the listener before returning, whatever the outcome.
    func waitForCode(expectedState: String, timeout: Duration = .seconds(120)) async throws -> String {
        let waiter = OneShot<String>()
        let queue = self.queue
        sink.setHandler { connection in
            Self.serve(connection, expectedState: expectedState, queue: queue, waiter: waiter)
        }
        let timer = Task {
            try? await Task.sleep(for: timeout)
            guard !Task.isCancelled else { return }
            waiter.finish(.failure(PIMError.network("Sign-in timed out")))
        }
        defer {
            timer.cancel()
            sink.clear()
            listener.cancel()
        }
        return try await withTaskCancellationHandler {
            try await waiter.value()
        } onCancel: {
            waiter.finish(.failure(CancellationError()))
        }
    }

    /// Cancels the listener without waiting (used when the flow fails before the redirect).
    func stop() {
        sink.clear()
        listener.cancel()
    }

    // MARK: Serving one request

    private static func serve(_ connection: NWConnection, expectedState: String, queue: DispatchQueue, waiter: OneShot<String>) {
        connection.start(queue: queue)
        read(connection, buffer: Data(), expectedState: expectedState, waiter: waiter)
    }

    private static func read(_ connection: NWConnection, buffer: Data, expectedState: String, waiter: OneShot<String>) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { data, _, isComplete, error in
            var buffer = buffer
            if let data { buffer.append(data) }
            if error != nil {
                connection.cancel()
                return
            }
            let text = String(decoding: buffer, as: UTF8.self)
            guard let headerEnd = text.range(of: "\r\n\r\n") ?? (isComplete ? text.endIndex..<text.endIndex : nil) else {
                guard buffer.count < 64 * 1024 else { connection.cancel(); return }
                read(connection, buffer: buffer, expectedState: expectedState, waiter: waiter)
                return
            }
            let requestLine = String(text[text.startIndex..<headerEnd.lowerBound]).split(separator: "\r\n").first.map(String.init) ?? ""
            handle(requestLine: requestLine, connection: connection, expectedState: expectedState, waiter: waiter)
        }
    }

    private static func handle(requestLine: String, connection: NWConnection, expectedState: String, waiter: OneShot<String>) {
        let target = requestLine.split(separator: " ").dropFirst().first.map(String.init) ?? "/"
        // Browsers ask for the icon on the same port; that request carries no OAuth response.
        guard !target.hasPrefix("/favicon.ico") else {
            respond(connection, status: "404 Not Found", body: "")
            return
        }
        let items = URLComponents(string: "http://localhost" + target)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }

        if let error = value("error") {
            let description = value("error_description") ?? error
            respond(connection, status: "400 Bad Request", body: page("Sign-in failed. You can close this window and return to Elevate."))
            waiter.finish(.failure(error == "access_denied" ? PIMError.network("Sign-in cancelled") : PIMError.network(description)))
            return
        }
        guard let state = value("state"), state == expectedState else {
            respond(connection, status: "400 Bad Request", body: page("Sign-in failed. You can close this window and return to Elevate."))
            waiter.finish(.failure(PIMError.unexpected(status: 0, body: "Sign-in state did not match; the response may not be ours")))
            return
        }
        guard let code = value("code") else {
            respond(connection, status: "400 Bad Request", body: page("Sign-in failed. You can close this window and return to Elevate."))
            waiter.finish(.failure(PIMError.network("Sign-in response carried no authorization code")))
            return
        }
        respond(connection, status: "200 OK", body: page("You can close this window and return to Elevate."))
        waiter.finish(.success(code))
    }

    private static func page(_ message: String) -> String {
        "<html><body style=\"font-family:-apple-system\">\(message)</body></html>"
    }

    private static func respond(_ connection: NWConnection, status: String, body: String) {
        let response = "HTTP/1.1 \(status)\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
        connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })
    }
}

/// Hands incoming connections to `waitForCode`, buffering any that arrive before it is ready.
private final class ConnectionSink: @unchecked Sendable {
    private let lock = NSLock()
    private var handler: (@Sendable (NWConnection) -> Void)?
    private var pending: [NWConnection] = []

    func accept(_ connection: NWConnection) {
        lock.lock()
        if let handler {
            lock.unlock()
            handler(connection)
        } else {
            pending.append(connection)
            lock.unlock()
        }
    }

    func setHandler(_ handler: @escaping @Sendable (NWConnection) -> Void) {
        lock.lock()
        self.handler = handler
        let queued = pending
        pending = []
        lock.unlock()
        queued.forEach(handler)
    }

    func clear() {
        lock.lock()
        handler = nil
        let queued = pending
        pending = []
        lock.unlock()
        queued.forEach { $0.cancel() }
    }
}

/// A result delivered exactly once, from a callback queue to an awaiting caller.
private final class OneShot<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Value, Error>?
    private var pending: Result<Value, Error>?
    private var delivered = false

    func value() async throws -> Value {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if let pending {
                self.pending = nil
                delivered = true
                lock.unlock()
                continuation.resume(with: pending)
            } else if delivered {
                lock.unlock()
                continuation.resume(throwing: CancellationError())
            } else {
                self.continuation = continuation
                lock.unlock()
            }
        }
    }

    func finish(_ result: Result<Value, Error>) {
        lock.lock()
        guard !delivered else { lock.unlock(); return }
        if let continuation {
            self.continuation = nil
            delivered = true
            lock.unlock()
            continuation.resume(with: result)
        } else {
            pending = result
            lock.unlock()
        }
    }
}
