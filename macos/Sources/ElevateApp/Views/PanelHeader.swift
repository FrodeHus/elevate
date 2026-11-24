import SwiftUI
import ElevateCore

/// The main column's title row: the Elevate mark on Home and the role list, the tenant on a tenant
/// view, then the select-roles toggle and refresh.
struct PanelHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var confirmRemove = false

    var body: some View {
        @Bindable var model = model
        HStack {
            if case .tenant(let key) = model.panelContent, let tenant = model.tenant(key) {
                tenantTitle(tenant)
            } else {
                // "Elevate" stays the primary mark at full weight; an organization's name is added
                // beneath it, never substituted for it (co-branding design 2026-09-18 §3.1). A second
                // line rather than an inline suffix: this row leaves the title about 26 characters,
                // which "Elevate - Managed by <name>" overruns for almost any real name.
                VStack(alignment: .leading, spacing: 0) {
                    Text("Elevate").font(.headline)
                    if let caption = model.branding?.headerCaption {
                        Text(caption)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.tail)
                    }
                }
                if !model.isOnline {
                    Text("offline").font(.caption2).foregroundStyle(.secondary)
                        .padding(.horizontal, 6).padding(.vertical, 1)
                        .background(.gray.opacity(0.25), in: Capsule())
                        .help("No network connection; refreshes and requests are paused")
                }
            }
            Spacer()
            Toggle(isOn: $model.selectMode) { Image(systemName: "checklist") }
                .toggleStyle(.button)
                .help("Select several roles to activate together")
                .accessibilityLabel("Select roles")
            Button { Task { await model.refreshAll(userInitiated: true) } } label: { Image(systemName: "arrow.clockwise") }
                .keyboardShortcut("r")
                .help("Refresh (⌘R)")
                .accessibilityLabel("Refresh")
                .disabled(!model.isOnline)
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private func tenantTitle(_ tenant: TenantContext) -> some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 6) {
                    Text(tenant.displayName).font(.headline).lineLimit(1)
                    if tenant.source == .home { Text("home").font(.caption2).foregroundStyle(.secondary) }
                }
                Text(model.identity(tenant.identityId)?.upn ?? tenant.identityId)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            if tenant.accessPackagesAvailable == true {
                Button("Access packages…") {
                    openWindow(value: PanelRoute.accessPackages(tenant.id))
                    NSApp.activate(ignoringOtherApps: true)
                }
                .controlSize(.small)
            }
            HeaderMenu(label: "Tenant actions") {
                TenantMenuItems(tenant: tenant, confirmRemove: $confirmRemove)
            }
        }
        .removeTenantConfirmation(tenant, isPresented: $confirmRemove)
    }
}
