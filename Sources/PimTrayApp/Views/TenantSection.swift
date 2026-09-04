import SwiftUI
import PimTrayCore

struct TenantSection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var expanded = true
    let tenant: TenantContext

    private var roles: [EligibleRole] { model.roles(for: tenant.id) }
    private var activeCount: Int { roles.filter { model.assignment(for: $0.key)?.status == .active }.count }

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            if roles.isEmpty {
                Text(tenant.discoveryMode == .manualRoles ? "No roles configured." : "No eligible roles.")
                    .font(.caption).foregroundStyle(.secondary).padding(.leading, 4)
            }
            ForEach(roles) { role in RoleRow(role: role) }
            if let err = model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError {
                Label(err, systemImage: "exclamationmark.circle").font(.caption).foregroundStyle(.orange)
            }
        } label: {
            HStack(spacing: 6) {
                Text(tenant.displayName).font(.subheadline)
                if tenant.source == .home { Text("home").font(.caption2).foregroundStyle(.secondary) }
                if tenant.discoveryMode == .manualRoles {
                    Text("manual roles").font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
                        .background(.orange.opacity(0.2), in: Capsule())
                }
                if model.busy.contains(tenant.id) { ProgressView().controlSize(.mini) }
                Spacer()
                if activeCount > 0 { Text("\(activeCount) active").font(.caption).foregroundStyle(.green) }
                Menu {
                    Button("Configure known PIM roles…") { open(.configureRoles(tenant.id)) }
                    Button("Retry discovery") { Task { await model.retryDiscovery(tenant.id) } }
                    if tenant.discoveryMode == .manualRoles, let url = model.adminConsentURL(tenantId: tenant.tenantId) {
                        Button("Open admin consent link…") { NSWorkspace.shared.open(url) }
                    }
                    Divider()
                    Button("Remove tenant", role: .destructive) { model.removeTenant(tenant.id) }
                        .disabled(tenant.source == .home)
                } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }
        }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
