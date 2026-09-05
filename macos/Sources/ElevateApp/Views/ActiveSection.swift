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
            // Yellow covers the pending states only: ActiveSummary.order drops .failed, so it never reaches the summary.
            Circle().fill(assignment.status == .active ? .green : .yellow).frame(width: 8, height: 8)
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
}
