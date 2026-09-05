import SwiftUI
import AppKit
import ElevateCore

/// Trailing controls for one assignment, shared by the role rows and the "Active now" summary.
struct AssignmentControls: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let key: RoleKey
    let assignment: ActiveAssignment?
    var viewOnlyReason: String? = nil
    /// Policy of the role behind this assignment; Extend is withheld when it needs approval.
    var policy: RolePolicy = .manualDefault
    /// The summary never offers Activate (its rows are already active or pending).
    var allowActivate: Bool = true

    var body: some View {
        if model.inFlight.contains(key) {
            ProgressView().controlSize(.small).help("Request in progress")
        } else {
            forStatus
        }
    }

    @ViewBuilder private var forStatus: some View {
        switch assignment?.status {
        case .active:
            let start = assignment?.startDateTime ?? .distantPast
            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                let lockedFor = 300 - ctx.date.timeIntervalSince(start)
                HStack(spacing: 8) {
                    Text(assignment?.endDateTime.flatMap { Countdown.remaining(until: $0, now: ctx.date) }.map(Countdown.label) ?? "")
                        .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        .frame(width: PanelMetrics.countdownWidth, alignment: .trailing)
                    if let a = assignment, lockedFor <= 0, ExtendWindow.canExtend(a, policy: policy, now: ctx.date) {
                        Button("Extend") {
                            if NSEvent.modifierFlags.contains(.option) {
                                Task { if await !model.quickActivate(key) { open(.activate([key])) } }
                            } else { open(.activate([key])) }
                        }
                            .controlSize(.small)
                            .disabled(!model.isOnline)
                            .help("Extend by activating again; Option-click to extend with the last reason")
                    }
                    Button("Deactivate") { Task { await model.deactivate(key) } }
                        .controlSize(.small)
                        .disabled(lockedFor > 0 || !model.isOnline)
                        .help(lockedFor > 0 ? "Can be deactivated in \(Int(lockedFor.rounded(.up))) s (Entra enforces 5 minutes)" : "Deactivate this role now")
                }
            }
        case .scheduled:
            let start = assignment?.startDateTime ?? .now
            TimelineView(.periodic(from: .now, by: 30)) { ctx in
                HStack(spacing: 8) {
                    Text("starts in \(Countdown.until(start, now: ctx.date))").font(.caption).foregroundStyle(.secondary)
                    Button("Cancel") { Task { await model.deactivate(key) } }.controlSize(.small).disabled(!model.isOnline)
                        .help("Cancel this scheduled activation")
                }
            }
        case .pendingApproval:
            Text("awaiting approval").font(.caption).foregroundStyle(.secondary)
            Button("Cancel") { Task { await model.cancelPending(key) } }
                .controlSize(.small).disabled(!model.isOnline).help("Withdraw this request")
        case .pendingProvisioning:
            ProgressView().controlSize(.small)
            Text("provisioning").font(.caption).foregroundStyle(.secondary)
        case .failed(let m):
            Text(m).font(.caption).foregroundStyle(.red).lineLimit(1)
        case nil:
            if let viewOnlyReason {
                Text("cannot activate").font(.caption).foregroundStyle(.secondary).help(viewOnlyReason)
            } else if allowActivate && !model.selectMode {
                Button("Activate") {
                    if NSEvent.modifierFlags.contains(.option) {
                        Task { if await !model.quickActivate(key) { open(.activate([key])) } }
                    } else { open(.activate([key])) }
                }
                .controlSize(.small).disabled(!model.isOnline)
                .help("Activate this role. Option-click to activate with the last reason and duration")
            }
        }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
