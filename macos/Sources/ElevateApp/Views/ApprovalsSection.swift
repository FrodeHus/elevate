import SwiftUI
import ElevateCore

/// Pinned "Approvals" list of requests awaiting this user's decision, above "Active now".
struct ApprovalsSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let items = model.approvalsOrdered
        if !items.isEmpty {
            Section {
                if !model.collapsedApprovals {
                    ForEach(items) { r in ApprovalRow(request: r) }
                }
            } header: {
                ApprovalsHeader(count: items.count)
            }
        }
    }
}

struct ApprovalsHeader: View {
    @Environment(AppModel.self) private var model
    let count: Int
    var body: some View {
        let expanded = !model.collapsedApprovals
        HStack(spacing: 6) {
            Button { withAnimation(.snappy) { model.toggleApprovals() } } label: {
                HStack(spacing: 6) {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                    Text("Approvals").font(.subheadline.weight(.semibold))
                    Text("\(count)").font(.caption).foregroundStyle(.orange)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(expanded ? "Collapse approvals" : "Expand approvals")
            Spacer()
        }
        .padding(.horizontal, PanelMetrics.headerInset)
        .padding(.vertical, 6)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
        .overlay(alignment: .bottom) { Divider() }
    }
}

struct ApprovalRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let request: ApprovalRequest

    /// Named style ("2 hours ago"); a formatter is expensive to build, so it is made once.
    private static let relative: RelativeDateTimeFormatter = {
        let f = RelativeDateTimeFormatter()
        f.dateTimeStyle = .named
        return f
    }()

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 8) {
                Image(systemName: "person.badge.clock").foregroundStyle(.orange)
                VStack(alignment: .leading, spacing: 1) {
                    Text(request.requesterName).font(.body.weight(.semibold)).lineLimit(1)
                    Text(target).font(.body).lineLimit(1).truncationMode(.middle)
                    Text(caption).font(.caption2).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                }
                Spacer()
                controls
            }
            if let error = model.approvalErrors[request.id] {
                Text(error).font(.caption2).foregroundStyle(.red).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(minHeight: 28)
        .padding(.vertical, 3)
        .padding(.leading, PanelMetrics.roleInset)
        .padding(.trailing, PanelMetrics.trailingInset)
        .help(request.justification ?? "No reason given")
    }

    private var target: String {
        if let scope = request.scopeCaption, !scope.isEmpty { return "\(request.targetName) · \(scope)" }
        return request.targetName
    }

    /// "<tenant> · <HH:MM> · <relative created time>"; the duration and time drop out when unknown.
    private var caption: String {
        var parts = [model.tenant(request.tenantKey)?.displayName ?? request.tenantKey.tenantId]
        if let d = request.requestedDuration { parts.append(Countdown.label(d)) }
        if let created = request.createdAt {
            parts.append(Self.relative.localizedString(for: created, relativeTo: model.clock))
        }
        return parts.joined(separator: " · ")
    }

    /// Only `.activate` can be decided through the APIs; extend/renew/other point at the portal.
    @ViewBuilder private var controls: some View {
        if model.decisionInFlight.contains(request.id) {
            ProgressView().controlSize(.small)
        } else if request.action == .activate {
            HStack(spacing: 6) {
                Button("Approve") { open(approve: true) }
                    .buttonStyle(.borderedProminent).controlSize(.small)
                Button("Deny") { open(approve: false) }
                    .controlSize(.small)
            }
        } else {
            Text("Decide in the portal").font(.caption2).foregroundStyle(.secondary)
        }
    }

    private func open(approve: Bool) {
        openWindow(value: PanelRoute.decide(requestId: request.id, approve: approve))
        NSApp.activate(ignoringOtherApps: true)
    }
}
