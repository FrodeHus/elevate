import SwiftUI
import ElevateCore

/// Pinned per-tenant header: carries the account caption above the tenant line so the account
/// context stays visible while its roles scroll underneath.
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
            // Accent only on the glyph: tinted text on a material reads badly in dark mode,
            // so the caption itself uses the standard secondary colour.
            HStack(spacing: 4) {
                Image(systemName: "person.crop.circle.fill").font(.caption2).foregroundStyle(Color.accentColor)
                Text(identity.upn).font(.caption2.weight(.medium)).lineLimit(1).truncationMode(.middle)
                if identity.signInMethod != .ownApp {
                    Text(identity.signInMethod.displayName).font(.caption2)
                }
            }
            .foregroundStyle(.secondary)
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
                if let reason = model.entraViewOnlyReason(for: tenant.id) { ViewOnlyBadge(reason: reason) }
                TenantPills(tenant: tenant)
                Spacer()
                if activeCount > 0 { Text("\(activeCount) active").font(.caption).foregroundStyle(.green) }
                HeaderMenu(label: "Tenant actions") {
                    TenantMenuItems(tenant: tenant)
                }
            }
        }
        .padding(.leading, PanelMetrics.headerInset)
        .padding(.trailing, PanelMetrics.trailingInset)
        .padding(.vertical, 5)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
        .overlay(alignment: .bottom) { Divider() }
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

/// The tenant's status pills: manual mode, Azure off, discovery/refresh error, busy spinner.
struct TenantPills: View {
    @Environment(AppModel.self) private var model
    let tenant: TenantContext
    var body: some View {
        if tenant.discoveryMode == .manualRoles {
            Text("manual roles").font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
                .background(.orange.opacity(0.2), in: Capsule())
        }
        if let reason = tenant.azureUnavailableReason {
            Text("Azure off").font(.caption2).foregroundStyle(.secondary).help(reason)
        }
        if let reason = tenant.groupsUnavailableReason {
            StatusPill(text: "Groups off", help: reason)
        }
        if let err = model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError {
            StatusPill(text: "error", tint: .red, help: err)
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
