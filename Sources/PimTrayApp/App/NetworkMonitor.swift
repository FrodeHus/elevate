import Foundation
import Network
import Observation

/// Reports whether the machine currently has a usable network path.
/// Starts optimistic so a first refresh is never suppressed while the monitor warms up.
@MainActor
@Observable
final class NetworkMonitor {
    private(set) var isOnline = true
    private let monitor = NWPathMonitor()

    init() {
        monitor.pathUpdateHandler = { path in
            let online = path.status == .satisfied
            Task { @MainActor [weak self] in self?.isOnline = online }
        }
        monitor.start(queue: DispatchQueue(label: "no.frodehus.elevate.network-monitor"))
    }

    deinit { monitor.cancel() }
}
