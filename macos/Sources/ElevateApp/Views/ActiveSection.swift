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
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var body: some View {
        PinnedSectionHeader(title: "Active now", accessibilityName: "active now", count: count, tint: .green,
                            expanded: !model.collapsedActive, onToggle: { withAnimation(reduceMotion ? nil : .snappy) { model.toggleActive() } })
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
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            AssignmentControls(key: key, assignment: assignment, policy: model.role(for: key)?.policy ?? .manualDefault, allowActivate: false)
        }
        .frame(minHeight: 28)
        .padding(.vertical, 3)
        .padding(.leading, PanelMetrics.roleInset)
        .padding(.trailing, PanelMetrics.trailingInset)
    }

    private var statusDot: some View {
        StatusDot(status: assignment.status)
    }
}

/// The 8 pt status glyph shared by role rows and the "Active now" summary. Colour is the cue for
/// sighted users; VoiceOver gets the state as a label instead of silence.
struct StatusDot: View {
    let status: ActiveAssignment.Status?

    var body: some View {
        Group {
            switch status {
            case .active: Circle().fill(.green)
            case .scheduled: Circle().fill(.blue)
            // Orange, not yellow: system yellow is close to invisible on a light ground.
            case .pendingApproval, .pendingProvisioning: Circle().fill(.orange)
            case .failed: Circle().fill(.red)
            case nil: Circle().stroke(.secondary)
            }
        }
        .frame(width: 8, height: 8)
        .accessibilityLabel(label)
    }

    private var label: String {
        switch status {
        case .active: "Active"
        case .scheduled: "Scheduled"
        case .pendingApproval: "Awaiting approval"
        case .pendingProvisioning: "Provisioning"
        case .failed: "Failed"
        case nil: "Eligible"
        }
    }
}
