import SwiftUI
import PimTrayCore

struct RoleRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let role: EligibleRole

    private var assignment: ActiveAssignment? { model.assignment(for: role.key) }

    var body: some View {
        HStack(spacing: 8) {
            if model.selectMode {
                Toggle("", isOn: Binding(get: { model.selection.contains(role.key) }, set: { _ in model.toggleSelection(role.key) }))
                    .labelsHidden()
                    .disabled(assignment?.status == .active)
            }
            statusDot
            VStack(alignment: .leading, spacing: 1) {
                Text(role.displayName).font(.body)
                if role.source == .manual { Text("manual").font(.caption2).foregroundStyle(.secondary) }
            }
            Spacer()
            trailing
        }
        .padding(.vertical, 3)
        .padding(.leading, 4)
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
        switch assignment?.status {
        case .active:
            if let end = assignment?.endDateTime {
                TimelineView(.periodic(from: .now, by: 1)) { ctx in
                    Text(Countdown.remaining(until: end, now: ctx.date).map(Countdown.label) ?? "expired")
                        .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
            }
            Button("Deactivate") { Task { await model.deactivate(role.key) } }
                .controlSize(.small)
        case .pendingApproval:
            Text("awaiting approval").font(.caption).foregroundStyle(.secondary)
        case .pendingProvisioning:
            Text("provisioning").font(.caption).foregroundStyle(.secondary)
        case .failed(let m):
            Text(m).font(.caption).foregroundStyle(.red).lineLimit(1)
        case nil:
            if !model.selectMode {
                Button("Activate") {
                    openWindow(value: PanelRoute.activate([role.key]))
                    NSApp.activate(ignoringOtherApps: true)
                }
                .controlSize(.small)
            }
        }
    }
}
