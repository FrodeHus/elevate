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

    private var roles: [EligibleRole] { model.roles(for: tenant.id) }
    private var activeCount: Int { roles.filter { model.assignment(for: $0.key)?.status == .active }.count }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Image(systemName: "person.crop.circle.fill").font(.caption2)
                Text(identity.upn).font(.caption2.weight(.medium)).lineLimit(1).truncationMode(.middle)
                if identity.signInMethod != .ownApp {
                    Text(identity.signInMethod.displayName).font(.caption2).foregroundStyle(.secondary)
                }
            }
            .foregroundStyle(Color.accentColor)
            HStack(spacing: 6) {
                Button { withAnimation(.snappy) { model.toggleTenant(tenant.id) } } label: {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                }
                .buttonStyle(.plain).accessibilityLabel(expanded ? "Collapse tenant" : "Expand tenant")
                Text(tenant.displayName).font(.subheadline)
                    .contentShape(Rectangle()).onTapGesture { withAnimation(.snappy) { model.toggleTenant(tenant.id) } }
                if tenant.source == .home { Text("home").font(.caption2).foregroundStyle(.secondary) }
                if tenant.discoveryMode == .manualRoles {
                    Text("manual roles").font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
                        .background(.orange.opacity(0.2), in: Capsule())
                }
                if let reason = tenant.azureUnavailableReason {
                    Text("Azure off").font(.caption2).foregroundStyle(.secondary).help(reason)
                }
                if let reason = model.entraViewOnlyReason(for: tenant.id) {
                    ViewOnlyBadge(reason: reason)
                }
                if model.busy.contains(tenant.id) { ProgressView().controlSize(.mini) }
                Spacer()
                if activeCount > 0 { Text("\(activeCount) active").font(.caption).foregroundStyle(.green) }
                Menu {
                    Button("Configure known PIM roles…") { open(.configureRoles(tenant.id)) }
                    Button("Retry discovery") { Task { await model.retryDiscovery(tenant.id) } }
                    if tenant.discoveryMode == .manualRoles,
                       let url = model.adminConsentURL(identityId: tenant.identityId, tenantId: tenant.tenantId) {
                        Button("Open admin consent link…") { NSWorkspace.shared.open(url) }
                    }
                    Divider()
                    Button("Remove tenant", role: .destructive) { model.removeTenant(tenant.id) }
                        .disabled(tenant.source == .home)
                } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .accessibilityLabel("Tenant actions")
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

/// The rows for one tenant: its roles, an empty-state caption, or a discovery error.
struct TenantRoles: View {
    @Environment(AppModel.self) private var model
    let tenant: TenantContext
    private var roles: [EligibleRole] { model.roles(for: tenant.id) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if roles.isEmpty {
                Text(tenant.discoveryMode == .manualRoles ? "No roles configured." : "No eligible roles.")
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(.leading, PanelMetrics.roleInset).padding(.vertical, 6)
            }
            ForEach(roles) { role in RoleRow(role: role) }
            if let err = model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError {
                Label(err, systemImage: "exclamationmark.circle").font(.caption).foregroundStyle(.orange)
                    .padding(.leading, PanelMetrics.roleInset).padding(.trailing, PanelMetrics.trailingInset).padding(.vertical, 4)
            }
        }
    }
}
