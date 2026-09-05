import SwiftUI
import ElevateCore

enum PanelMetrics {
    static let width: CGFloat = 380
    static let maxListHeight: CGFloat = 460
    static let headerInset: CGFloat = 12      // account row, tenant header
    static let roleInset: CGFloat = 28        // role rows: status-dot column
    static let trailingInset: CGFloat = 12
    static let countdownWidth: CGFloat = 44
}

struct PanelView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var contentHeight: CGFloat = 0

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            header
            Divider()
            if let notice = model.notice {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "exclamationmark.circle").foregroundStyle(.orange)
                    Text(notice).font(.caption).textSelection(.enabled)
                    Spacer()
                    Button { model.notice = nil } label: { Image(systemName: "xmark") }
                        .buttonStyle(.borderless).help("Dismiss").accessibilityLabel("Dismiss")
                }
                .padding(.horizontal, 12).padding(.vertical, 6)
                .background(.orange.opacity(0.12))
                Divider()
            }
            if !model.isConfigured && model.identities.isEmpty {
                SetupView()
            } else if let fatal = model.startupError {
                ContentUnavailableView("Elevate cannot start", systemImage: "exclamationmark.triangle", description: Text(fatal))
                    .frame(height: 200)
            } else if model.identities.isEmpty {
                ContentUnavailableView("No accounts", systemImage: "person.crop.circle.badge.plus",
                                       description: Text("Add an account to see your PIM roles."))
                    .frame(height: 160)
            } else {
                // A ScrollView in a MenuBarExtra window reports no ideal height, so the panel would collapse
                // to zero; size it from the measured content instead, capped so long lists scroll.
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                        ForEach(model.identities) { identity in
                            IdentityHeader(identity: identity)
                            ForEach(model.tenants(for: identity.id)) { tenant in
                                TenantBlock(identity: identity, tenant: tenant)
                            }
                        }
                    }
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { contentHeight = $0 }
                }
                .frame(height: min(max(contentHeight, 44), PanelMetrics.maxListHeight))
            }
            if model.selectMode {
                Divider()
                Button {
                    openWindow(value: PanelRoute.activate(Array(model.selection).sorted { "\($0)" < "\($1)" }))
                    NSApp.activate(ignoringOtherApps: true)
                } label: {
                    Text("Activate \(model.selection.count) role\(model.selection.count == 1 ? "" : "s")")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .disabled(model.selection.isEmpty)
                .padding(10)
            }
            Divider()
            footer
        }
        .frame(width: PanelMetrics.width)
    }

    private var header: some View {
        @Bindable var model = model
        return HStack {
            Text("Elevate").font(.headline)
            if !model.isOnline {
                Text("offline").font(.caption2).foregroundStyle(.secondary)
                    .padding(.horizontal, 6).padding(.vertical, 1)
                    .background(.gray.opacity(0.25), in: Capsule())
                    .help("No network connection; refreshes and requests are paused")
            }
            Spacer()
            Toggle(isOn: $model.selectMode) { Image(systemName: "checklist") }
                .toggleStyle(.button)
                .help("Select several roles to activate together")
                .accessibilityLabel("Select roles")
            Button { Task { await model.refreshAll() } } label: { Image(systemName: "arrow.clockwise") }
                .help("Refresh")
                .accessibilityLabel("Refresh")
                .disabled(!model.isOnline)
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var footer: some View {
        HStack {
            Button("Add account…") {
                openWindow(value: PanelRoute.addAccount)
                NSApp.activate(ignoringOtherApps: true)
            }
            Spacer()
            SettingsLink { Text("Settings…") }.buttonStyle(.borderless)
            Spacer()
            Button("Quit") { NSApp.terminate(nil) }
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}

/// A tenant's pinned header plus its role rows, as one `Section` so the header sticks while its rows scroll.
struct TenantBlock: View {
    let identity: Identity
    let tenant: TenantContext
    @State private var expanded = true

    var body: some View {
        Section {
            if expanded { TenantRoles(tenant: tenant) }
        } header: {
            TenantHeader(tenant: tenant, identity: identity, expanded: $expanded)
        }
    }
}
