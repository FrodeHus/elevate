import SwiftUI
import AppKit
import ElevateCore

/// The role list: every account and tenant (`tenant == nil`), or one tenant's approvals, active
/// assignments and eligible roles.
struct RoleListView: View {
    @Environment(AppModel.self) private var model
    let tenant: TenantKey?
    /// Height the panel leaves for this list; it scrolls beyond that.
    var availableHeight: CGFloat = PanelMetrics.maxHeight
    @State private var contentHeight: CGFloat = 0
    @State private var tabsHeight: CGFloat = 0
    @State private var listGeneration = 0

    var body: some View {
        VStack(spacing: 0) {
            if tenant == nil {
                tabs.onGeometryChange(for: CGFloat.self) { $0.size.height } action: { tabsHeight = $0 }
            }
            // A ScrollView in a MenuBarExtra window reports no ideal height, so the panel would collapse
            // to zero; size it from the measured content instead, capped so long lists scroll.
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                        if let tenant { tenantContent(tenant) } else { allTenantsContent }
                    }
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { contentHeight = $0 }
                }
                .frame(height: min(max(contentHeight, 44), max(availableHeight - tabsHeight, 44)))
                // The lazy stack can come back from display sleep with no rows realised, and its height depends on them.
                .id(listGeneration)
                .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.screensDidWakeNotification)) { _ in listGeneration += 1 }
                .onChange(of: model.highlightedRole) { _, key in if let key { proxy.scrollTo(key) } }
            }
        }
    }

    private var tabs: some View {
        VStack(spacing: 0) {
            Picker("", selection: Binding(get: { model.panelTab }, set: { model.panelTab = $0 })) {
                // No counts in the segments: the Active now header and the tenant rows already
                // count, and a number here made the segments change width with every activation.
                ForEach(PanelTab.allCases, id: \.self) { tab in
                    Text(tab.title).tag(tab)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            Divider()
        }
    }

    @ViewBuilder private var allTenantsContent: some View {
        let visible = model.visibleIdentities
        if visible.isEmpty {
            Text("No matches").font(.caption).foregroundStyle(.secondary).padding(12)
        }
        ForEach(visible) { identity in
            let tenants = model.visibleTenants(for: identity.id)
            if tenants.count == 1, let only = tenants.first {
                // One tenant: fold it into the account row (pinned) and skip the tenant header.
                Section {
                    if !model.collapsedIdentities.contains(identity.id) { TenantRoles(tenant: only) }
                } header: {
                    IdentityHeader(identity: identity, soleTenant: only)
                }
            } else {
                IdentityHeader(identity: identity)
                if !model.collapsedIdentities.contains(identity.id) {
                    ForEach(tenants) { tenant in
                        TenantBlock(identity: identity, tenant: tenant)
                    }
                }
            }
        }
    }

    @ViewBuilder private func tenantContent(_ key: TenantKey) -> some View {
        ApprovalsSection(tenant: key)
        ActiveSection(tenant: key)
        tabs
        if let context = model.tenant(key) {
            let rows = model.listRows(for: key)
            let others = model.otherTenantMatchCount
            Section {
                if model.isFiltering && rows.isEmpty {
                    Text("No matches").font(.caption).foregroundStyle(.secondary)
                        .padding(.leading, PanelMetrics.roleInset).padding(.vertical, 6)
                } else {
                    TenantRoles(tenant: context)
                }
                if others > 0 {
                    Button { model.searchAllTenants() } label: {
                        Text("\(others) more in other tenants").font(.caption)
                            .padding(.leading, PanelMetrics.roleInset).padding(.vertical, 6)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).foregroundStyle(.tint)
                }
            } header: {
                Text("Eligible · \(rows.count)").font(.subheadline.weight(.semibold))
                    .pinnedHeaderChrome()
            }
        }
    }
}
