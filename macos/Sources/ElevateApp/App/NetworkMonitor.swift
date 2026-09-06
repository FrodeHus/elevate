import Foundation
import Network
import Observation

/// Reports whether the machine currently has a usable network path.
/// Starts optimistic so a first refresh is never suppressed while the monitor warms up.
@MainActor
@Observable
final class NetworkMonitor {
    private(set) var isOnline = true
    private let monitor: NWPathMonitor?

    /// - Parameter forcedOnline: nil (the default) watches the real network path. A non-nil value
    ///   pins `isOnline` and starts no monitor at all, which is how tests keep the model offline.
    init(forcedOnline: Bool? = nil) {
        guard let forcedOnline else {
            let monitor = NWPathMonitor()
            self.monitor = monitor
            monitor.pathUpdateHandler = { path in
                let online = path.status == .satisfied
                Task { @MainActor [weak self] in self?.isOnline = online }
            }
            monitor.start(queue: DispatchQueue(label: "no.reothor.elevate.network-monitor"))
            return
        }
        monitor = nil
        isOnline = forcedOnline
    }

    deinit { monitor?.cancel() }
}
