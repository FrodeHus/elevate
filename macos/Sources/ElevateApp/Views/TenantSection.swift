import SwiftUI
import ElevateCore

/// Pinned per-tenant header: one line with the tenant name, its status and the tenant menu. The
/// account is named once on its own row above; repeating it here read as noise.
struct TenantHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let tenant: TenantContext
    let identity: Identity
    private var expanded: Bool { !model.collapsedTenants.contains(tenant.id) }

    // Counts the current tab's kinds only, so the number matches the rows under the header
    // (the tab labels carry the per-kind totals).
    private var activeCount: Int {
        model.roles(for: tenant.id, tab: model.panelTab).filter { model.assignment(for: $0.key)?.status == .active }.count
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                // One plain button for chevron + name: a bare tap gesture next to a borderless Menu
                // loses to the menu's hit area, which stretches across the row.
                Button { withAnimation(.snappy) { model.toggleTenant(tenant.id) } } label: {
                    HStack(spacing: 6) {
                        Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                            .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                        Text(tenant.displayName).font(.subheadline)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(expanded ? "Collapse tenant" : "Expand tenant")
                if tenant.source == .home { Text("home").font(.caption2).foregroundStyle(.secondary) }
                TenantPills(tenant: tenant)
                Spacer()
                if activeCount > 0 { Text("\(activeCount) active").font(.caption).foregroundStyle(.green) }
                HeaderMenu(label: "Tenant actions") {
                    TenantMenuItems(tenant: tenant)
                }
            }
        }
        .pinnedHeaderChrome()
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}

/// The rows for one tenant: its roles or an empty-state caption. Errors live in the header pill.
struct TenantRoles: View {
    @Environment(AppModel.self) private var model
    let tenant: TenantContext
    private var roles: [EligibleRole] { model.roles(for: tenant.id, tab: model.panelTab) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if model.panelTab == .groups, let reason = model.groupsUnavailableReason(for: tenant.id) {
                Label(reason, systemImage: "info.circle").font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.leading, PanelMetrics.roleInset).padding(.trailing, PanelMetrics.trailingInset).padding(.vertical, 6)
            } else if roles.isEmpty {
                Text(emptyText).font(.caption).foregroundStyle(.secondary)
                    .padding(.leading, PanelMetrics.roleInset).padding(.vertical, 6)
            }
            ForEach(roles) { role in RoleRow(role: role) }
        }
    }

    private var emptyText: String {
        switch (model.panelTab, tenant.discoveryMode) {
        case (.groups, _): "No eligible groups."
        case (.azure, .manualRoles): "No roles configured."
        case (.azure, _): tenant.azureUnavailableReason ?? "No eligible Azure resource roles."
        case (.roles, .manualRoles): "No roles configured."
        case (.roles, .automatic):
            model.entraViewOnlyReason(for: tenant.id) != nil
                ? "This account supports Azure resource roles only; see the Azure tab."
                : "No eligible Entra roles."
        }
    }
}

/// The tenant's status: a "manual roles" pill (a mode, not a problem), then one warning glyph in
/// place of a run of pills for everything that limits this tenant — hover for a summary, click for
/// the full list — and a spinner while busy.
struct TenantPills: View {
    @Environment(AppModel.self) private var model
    let tenant: TenantContext
    @State private var showingIssues = false

    private struct Issue: Identifiable { let title: String; let detail: String; var id: String { title } }

    private var issues: [Issue] {
        var out: [Issue] = []
        if let r = model.entraViewOnlyReason(for: tenant.id) { out.append(Issue(title: "Entra roles are view-only", detail: r)) }
        if let r = tenant.azureUnavailableReason { out.append(Issue(title: "Azure resource roles are off", detail: r)) }
        if let r = tenant.groupsUnavailableReason { out.append(Issue(title: "PIM for Groups is off", detail: r)) }
        if let e = model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError { out.append(Issue(title: "Discovery or refresh failed", detail: e)) }
        return out
    }
    private var hasError: Bool { (model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError) != nil }

    var body: some View {
        if tenant.discoveryMode == .manualRoles {
            Text("manual roles").font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
                .background(.orange.opacity(0.2), in: Capsule())
        }
        let issues = issues
        if !issues.isEmpty {
            Button { showingIssues.toggle() } label: {
                Image(systemName: hasError ? "exclamationmark.triangle.fill" : "exclamationmark.circle.fill")
                    .font(.caption).foregroundStyle(hasError ? .red : .orange)
                    .frame(width: 16, height: 16).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(issues.map(\.title).joined(separator: "\n"))
            .accessibilityLabel(issues.count == 1 ? "1 limitation" : "\(issues.count) limitations")
            .popover(isPresented: $showingIssues, arrowEdge: .bottom) {
                VStack(alignment: .leading, spacing: 10) {
                    ForEach(issues) { issue in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(issue.title).font(.subheadline.weight(.semibold))
                            Text(issue.detail).font(.caption).foregroundStyle(.secondary)
                                .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }
                .padding(12).frame(width: 320, alignment: .leading)
            }
        }
        if model.busy.contains(tenant.id) { ProgressView().controlSize(.mini) }
    }
}

/// Menu entries that act on one tenant; shared by the tenant header and the single-tenant account row.
struct TenantMenuItems: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let tenant: TenantContext
    var body: some View {
        Button("Configure known PIM roles…") {
            openWindow(value: PanelRoute.configureRoles(tenant.id))
            NSApp.activate(ignoringOtherApps: true)
        }
        Button("Retry discovery") { Task { await model.retryDiscovery(tenant.id) } }
        if tenant.discoveryMode == .manualRoles || tenant.groupsUnavailableReason != nil,
           let url = model.adminConsentURL(identityId: tenant.identityId, tenantId: tenant.tenantId) {
            Button("Open admin consent link…") { NSWorkspace.shared.open(url) }
        }
        Divider()
        Button("Remove tenant", role: .destructive) { model.removeTenant(tenant.id) }
            .disabled(tenant.source == .home)
    }
}
