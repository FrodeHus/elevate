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
                    .accessibilityLabel("Select \(role.displayName)")
                    .disabled(assignment?.status == .active || viewOnlyReason != nil)
                    .help(viewOnlyReason ?? "")
            }
            statusDot
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 6) {
                    RoleNameLabel(role.displayName, isPrivileged: role.isPrivileged).font(.body)
                    if model.isNewRole(role.key) {
                        Text("new").font(.caption2.weight(.medium))
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(Color.accentColor.opacity(0.18), in: Capsule())
                            .accessibilityLabel("New role")
                    }
                }
                if let detail = role.detail {
                    Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1).help(scopeTooltip ?? detail)
                }
                if let via = role.viaGroup {
                    Text(via == "group" ? "via group" : "via \(via)").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        .help("This eligibility is granted through a group; activating it activates the role for you")
                }
                if model.panelTab == .all, let caption = model.groupCaption(role.key) {
                    Text(caption).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
                if role.source == .manual { Text("manual").font(.caption2).foregroundStyle(.secondary) }
                if assignment == nil, let notes = PolicyNotes.caption(for: role.policy) {
                    Text(notes).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        .help(PolicyNotes.explanation(for: role.policy) ?? "")
                        .accessibilityLabel("Policy: \(notes)")
                }
            }
            Spacer()
            AssignmentControls(key: role.key, assignment: assignment, viewOnlyReason: viewOnlyReason, policy: role.policy)
        }
        .frame(minHeight: 28)
        .padding(.vertical, 3)
        .padding(.leading, PanelMetrics.roleInset)
        .padding(.trailing, PanelMetrics.trailingInset)
        .background(Color.accentColor.opacity(highlighted ? 0.25 : model.isNewRole(role.key) ? 0.08 : 0), in: RoundedRectangle(cornerRadius: 6))
        .accessibilityAddTraits(highlighted ? .isSelected : [])
        .id(role.key) // the arrow keys scroll the highlighted row into view
        .contentShape(Rectangle())
        .contextMenu {
            let favorite = model.isFavorite(role.key)
            Button(favorite ? "Remove from favorites" : "Add to favorites") {
                // The scope is part of the name: "Contributor" alone does not tell two favorites apart.
                model.setFavorite(role.key, name: role.detail.map { "\(role.displayName) · \($0)" } ?? role.displayName, !favorite)
            }
        }
    }

    private var highlighted: Bool { model.highlightedRole == role.key }

    private var statusDot: some View {
        RoleStatusIndicator(status: assignment?.status, deactivation: model.deactivationProgress[role.key],
                            propagating: model.isPropagating(role.key))
    }

    /// Azure captions are shortened to the scope's display name; the full ARM path is one hover away.
    private var scopeTooltip: String? {
        if case .azureResource(let scope, _) = role.key.scope { return scope }
        return nil
    }
}
