import SwiftUI
import ElevateCore

/// Pinned "Active now" summary across all accounts and tenants, above the account list.
struct ActiveSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let items = model.activeAssignmentsOrdered
        if !items.isEmpty {
            Section {
                if !model.collapsedActive {
                    ForEach(items) { a in ActiveRow(assignment: a) }
                }
            } header: {
                ActiveHeader(count: items.count)
            }
        }
    }
}

struct ActiveHeader: View {
    @Environment(AppModel.self) private var model
    let count: Int
    var body: some View {
        let expanded = !model.collapsedActive
        HStack(spacing: 6) {
            Button { withAnimation(.snappy) { model.toggleActive() } } label: {
                HStack(spacing: 6) {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                    Text("Active now").font(.subheadline.weight(.semibold))
                    Text("\(count)").font(.caption).foregroundStyle(.green)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(expanded ? "Collapse active now" : "Expand active now")
            Spacer()
        }
        .padding(.horizontal, PanelMetrics.headerInset)
        .padding(.vertical, 6)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
        .overlay(alignment: .bottom) { Divider() }
    }
}

struct ActiveRow: View {
    @Environment(AppModel.self) private var model
    let assignment: ActiveAssignment

    var body: some View {
        let key = assignment.roleKey
        HStack(spacing: 8) {
            statusDot
            VStack(alignment: .leading, spacing: 1) {
                Text(model.summaryName(for: key)).font(.body).lineLimit(1)
                Text("\(model.tenant(key.tenantKey)?.displayName ?? key.tenantId) · \(model.identity(key.identityId)?.upn ?? key.identityId)")
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            AssignmentControls(key: key, assignment: assignment, policy: model.role(for: key)?.policy ?? .manualDefault, allowActivate: false)
        }
        .frame(minHeight: 28)
        .padding(.vertical, 3)
        .padding(.leading, PanelMetrics.roleInset)
        .padding(.trailing, PanelMetrics.trailingInset)
    }

    // ActiveSummary.order drops .failed, so red never actually reaches the summary; it is here for completeness.
    @ViewBuilder private var statusDot: some View {
        switch assignment.status {
        case .active: Circle().fill(.green).frame(width: 8, height: 8)
        case .scheduled: Circle().fill(.blue).frame(width: 8, height: 8)
        case .pendingApproval, .pendingProvisioning: Circle().fill(.yellow).frame(width: 8, height: 8)
        case .failed: Circle().fill(.red).frame(width: 8, height: 8)
        }
    }
}
