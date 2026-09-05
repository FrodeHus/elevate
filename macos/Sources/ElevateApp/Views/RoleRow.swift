import SwiftUI
import ElevateCore

struct RoleRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let role: EligibleRole

    private var assignment: ActiveAssignment? { model.assignment(for: role.key) }
    private var viewOnlyReason: String? {
        role.key.scope.kind == .entraDirectory ? model.entraViewOnlyReason(for: role.key.tenantKey) : nil
    }

    var body: some View {
        HStack(spacing: 8) {
            if model.selectMode {
                Toggle("", isOn: Binding(get: { model.selection.contains(role.key) }, set: { _ in model.toggleSelection(role.key) }))
                    .labelsHidden()
                    .accessibilityLabel("Select role")
                    .disabled(assignment?.status == .active || viewOnlyReason != nil)
                    .help(viewOnlyReason ?? "")
            }
            statusDot
            VStack(alignment: .leading, spacing: 1) {
                Text(role.displayName).font(.body)
                if let detail = role.detail { Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1) }
                if let via = role.viaGroup {
                    Text(via == "group" ? "via group" : "via \(via)").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        .help("This eligibility is granted through a group; activating it activates the role for you")
                }
                if role.source == .manual { Text("manual").font(.caption2).foregroundStyle(.secondary) }
            }
            Spacer()
            trailing
        }
        .frame(minHeight: 28)
        .padding(.vertical, 3)
        .padding(.leading, PanelMetrics.roleInset)
        .padding(.trailing, PanelMetrics.trailingInset)
    }

    @ViewBuilder private var statusDot: some View {
        switch assignment?.status {
        case .active: Circle().fill(.green).frame(width: 8, height: 8)
        case .pendingApproval, .pendingProvisioning: Circle().fill(.yellow).frame(width: 8, height: 8)
        case .failed: Circle().fill(.red).frame(width: 8, height: 8)
        case nil: Circle().stroke(.secondary).frame(width: 8, height: 8)
        }
    }

    @ViewBuilder private var trailing: some View {
        if model.inFlight.contains(role.key) {
            ProgressView().controlSize(.small).help("Request in progress")
        } else {
            trailingForStatus
        }
    }

    @ViewBuilder private var trailingForStatus: some View {
        switch assignment?.status {
        case .active:
            // Entra refuses deactivation within 5 minutes of activation; hold the button until then.
            let start = assignment?.startDateTime ?? .distantPast
            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                let lockedFor = 300 - ctx.date.timeIntervalSince(start)
                // One horizontal line: countdown, then the button. Multiple children of a TimelineView stack vertically otherwise.
                HStack(spacing: 8) {
                    Text(assignment?.endDateTime.flatMap { Countdown.remaining(until: $0, now: ctx.date) }.map(Countdown.label) ?? "")
                        .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        .frame(width: PanelMetrics.countdownWidth, alignment: .trailing)
                    Button("Deactivate") { Task { await model.deactivate(role.key) } }
                        .controlSize(.small)
                        .disabled(lockedFor > 0 || !model.isOnline)
                        .help(lockedFor > 0 ? "Can be deactivated in \(Int(lockedFor.rounded(.up))) s (Entra enforces 5 minutes)" : "Deactivate this role now")
                }
            }
        case .pendingApproval:
            Text("awaiting approval").font(.caption).foregroundStyle(.secondary)
            Button("Cancel") { Task { await model.cancelPending(role.key) } }
                .controlSize(.small)
                .disabled(!model.isOnline)
                .help("Withdraw this request")
        case .pendingProvisioning:
            ProgressView().controlSize(.small)
            Text("provisioning").font(.caption).foregroundStyle(.secondary)
        case .failed(let m):
            Text(m).font(.caption).foregroundStyle(.red).lineLimit(1)
        case nil:
            if let viewOnlyReason {
                Text("cannot activate").font(.caption).foregroundStyle(.secondary).help(viewOnlyReason)
            } else if !model.selectMode {
                Button("Activate") {
                    openWindow(value: PanelRoute.activate([role.key]))
                    NSApp.activate(ignoringOtherApps: true)
                }
                .controlSize(.small)
                .disabled(!model.isOnline)
            }
        }
    }
}
