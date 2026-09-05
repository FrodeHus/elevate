import SwiftUI
import ElevateCore

struct RoleRow: View {
    @Environment(AppModel.self) private var model
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
                if let detail = role.detail {
                    Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1).help(scopeTooltip ?? detail)
                }
                if let via = role.viaGroup {
                    Text(via == "group" ? "via group" : "via \(via)").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        .help("This eligibility is granted through a group; activating it activates the role for you")
                }
                if role.source == .manual { Text("manual").font(.caption2).foregroundStyle(.secondary) }
            }
            Spacer()
            AssignmentControls(key: role.key, assignment: assignment, viewOnlyReason: viewOnlyReason)
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

    /// Azure captions are shortened to the scope's display name; the full ARM path is one hover away.
    private var scopeTooltip: String? {
        if case .azureResource(let scope, _) = role.key.scope { return scope }
        return nil
    }
}
