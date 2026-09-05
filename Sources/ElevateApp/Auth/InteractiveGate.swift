import Foundation

/// Serialises interactive sign-in sessions. MSAL refuses a second one with
/// `MSALErrorInteractiveSessionAlreadyRunning`, and a second loopback flow would race for the
/// browser and the listener, so callers queue instead of failing. Shared by both providers.
actor InteractiveGate {
    private var busy = false
    private var waiters: [(id: UUID, continuation: CheckedContinuation<Bool, Never>)] = []

    /// Runs `body` with the gate held, throwing `CancellationError` if the wait is cancelled.
    /// `body` must not call another gated method: the gate is not reentrant, so a nested call
    /// would wait forever for the lock its own caller is holding.
    func run<T: Sendable>(_ body: @Sendable () async throws -> T) async throws -> T {
        try await acquire()
        defer { release() }
        return try await body()
    }

    private func acquire() async throws {
        guard busy else { busy = true; return }
        guard !Task.isCancelled else { throw CancellationError() }
        let id = UUID()
        let owned = await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                waiters.append((id, continuation))
            }
        } onCancel: {
            Task { await self.dropWaiter(id) }
        }
        guard owned else { throw CancellationError() }
        // Cancelled just as the gate was handed over: pass it straight on rather than hold it.
        if Task.isCancelled {
            release()
            throw CancellationError()
        }
    }

    /// Takes a cancelled waiter out of the queue; it never becomes the owner.
    private func dropWaiter(_ id: UUID) {
        guard let index = waiters.firstIndex(where: { $0.id == id }) else { return }
        waiters.remove(at: index).continuation.resume(returning: false)
    }

    private func release() {
        if waiters.isEmpty { busy = false } else { waiters.removeFirst().continuation.resume(returning: true) }
    }
}
