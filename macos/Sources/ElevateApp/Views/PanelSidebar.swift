import SwiftUI
import AppKit
import ElevateCore

/// The 136-pt column beside the main view: pinned profiles on top (fixed), every account with its
/// tenants in the middle (scrolls), and Add account… at the bottom (fixed).
struct PanelSidebar: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var profilesHeight: CGFloat = 0
    @State private var footerHeight: CGFloat = 0
    @State private var tenantsHeight: CGFloat = 0

    private static let inset: CGFloat = 6

    var body: some View {
        let showAccounts = model.isConfigured && !model.identities.isEmpty
        VStack(spacing: 0) {
            if showAccounts {
                if !model.profiles.isEmpty {
                    VStack(spacing: 0) {
                        ProfilesSection()
                        Divider().padding(.horizontal, 2).padding(.bottom, 4)
                    }
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { profilesHeight = $0 }
                } else {
                    Color.clear.frame(height: 0).onAppear { profilesHeight = 0 }
                }
                // A ScrollView reports no ideal height here, so size it from its measured content,
                // capped so the whole panel stays within `PanelMetrics.maxHeight`; it grows past
                // that only to fill a taller main column.
                let cap = max(PanelMetrics.maxHeight - profilesHeight - footerHeight, 44)
                ScrollView {
                    TenantsSection()
                        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { tenantsHeight = $0 }
                }
                .frame(minHeight: min(max(tenantsHeight, 44), cap), maxHeight: .infinity)
            } else {
                Spacer(minLength: 0)
            }
            addAccount
                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { footerHeight = $0 }
        }
        .padding(.horizontal, Self.inset)
        .padding(.top, 8)
        .frame(maxHeight: PanelMetrics.maxHeight)
        .background(Color.primary.opacity(0.04))
        .overlay(alignment: .trailing) { Divider() }
    }

    private var addAccount: some View {
        VStack(spacing: 0) {
            Divider().padding(.horizontal, 2)
            SidebarRowButton(accessibilityLabel: "Add account", action: {
                openWindow(value: PanelRoute.addAccount)
                NSApp.activate(ignoringOtherApps: true)
            }) {
                Image(systemName: "plus").font(.caption.weight(.semibold)).frame(width: 12)
                Text("Add account…").lineLimit(1)
            }
            .foregroundStyle(.secondary)
            .padding(.vertical, 6)
        }
    }
}

/// A full-width sidebar row: a plain button with a fixed height, an optional selected fill and the
/// state in its accessibility label.
private struct SidebarRowButton<Label: View>: View {
    var selected = false
    var height: CGFloat = 28
    var tint: Color? = nil
    let accessibilityLabel: String
    let action: () -> Void
    @ViewBuilder let label: () -> Label
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) { label() }
                .font(.callout)
                .padding(.horizontal, 8)
                .frame(maxWidth: .infinity, minHeight: height, maxHeight: height, alignment: .leading)
                .foregroundStyle(selected ? Color.white : (tint ?? Color.primary))
                .background(selected ? Color.accentColor : (hovering ? Color.primary.opacity(0.07) : .clear),
                            in: RoundedRectangle(cornerRadius: 7, style: .continuous))
                .onHover { hovering = $0 }
                .contentShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(accessibilityLabel)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

}

/// A clickable section title with a disclosure chevron; `badge` is shown while the section is collapsed.
private struct SectionHeader<Badge: View>: View {
    let title: LocalizedStringKey
    let accessibilityName: String
    @Binding var collapsed: Bool
    @ViewBuilder let badge: () -> Badge
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Button { withAnimation(reduceMotion ? nil : .snappy) { collapsed.toggle() } } label: {
            HStack(spacing: 4) {
                Image(systemName: "chevron.right").rotationEffect(.degrees(collapsed ? 0 : 90))
                    .font(.system(size: 8, weight: .semibold)).frame(width: 8)
                Text(title).textCase(.uppercase).font(.caption2.weight(.semibold))
                Spacer(minLength: 4)
                if collapsed { badge() }
            }
            .foregroundStyle(.secondary)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(collapsed ? "Expand \(accessibilityName)" : "Collapse \(accessibilityName)")
        .accessibilityValue(collapsed ? "collapsed" : "expanded")
    }
}

// MARK: Profiles

private struct ProfilesSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        let selection = model.panelSelection
        let pinned = model.pinnedProfiles
        let pinnedSelected = pinned.contains { selection == .profile($0.id) }
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                SectionHeader(title: "Profiles", accessibilityName: "profiles",
                              collapsed: Binding(get: { model.settings.sidebarProfilesCollapsed }, set: { model.settings.sidebarProfilesCollapsed = $0 })) {
                    Text("\(model.profiles.count)").font(.caption).monospacedDigit()
                }
                if !model.settings.sidebarProfilesCollapsed {
                    Button { model.allProfilesOpen.toggle() } label: {
                        Text("All \(model.profiles.count)").font(.caption)
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .foregroundStyle(!pinnedSelected && selection.isProfile ? Color.white : Color.secondary)
                            .background(!pinnedSelected && selection.isProfile ? Color.accentColor : .clear, in: Capsule())
                            .contentShape(Capsule())
                    }
                    .buttonStyle(.plain)
                    .help("All profiles: search, run, pin and manage")
                    .accessibilityLabel("All profiles, \(model.profiles.count)")
                    .popover(isPresented: $model.allProfilesOpen, arrowEdge: .trailing) {
                        AllProfilesPopover(dismiss: { model.allProfilesOpen = false })
                    }
                }
            }
            .padding(.horizontal, 8)
            if !model.settings.sidebarProfilesCollapsed {
                ForEach(pinned) { ProfileButton(profile: $0) }
            }
        }
        .padding(.bottom, 8)
    }
}

private extension PanelSelection {
    var isProfile: Bool { if case .profile = self { true } else { false } }
}

private struct ProfileButton: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let profile: ActivationProfile

    private var help: String {
        var text = "\(profile.name) (\(ProfileSummary.caption(entries: profile.entries)))"
        if model.settings.hotKeyProfileId == profile.id, let hotKey = model.settings.hotKey { text += " · \(hotKey.display)" }
        return text + "\nClick to open. Option-click to run with the last reason and durations"
    }

    var body: some View {
        SidebarRowButton(selected: model.panelSelection == .profile(profile.id), height: 28,
                         accessibilityLabel: "\(profile.name), \(ProfileSummary.caption(entries: profile.entries))",
                         action: {
            ProfileActions.run(profile.id, model: model, openWindow: openWindow,
                               silentlyIfPossible: NSEvent.modifierFlags.contains(.option), inPanel: true)
        }) {
            Image(systemName: profile.entries.count == 1 ? "star.fill" : "bolt.fill").font(.caption2)
                .foregroundStyle(model.panelSelection == .profile(profile.id) ? Color.white : Color.secondary).frame(width: 12)
            Text(profile.name).lineLimit(1).truncationMode(.tail)
        }
        .help(help)
        .contextMenu { ProfileMenuItems(profile: profile) }
    }
}

// MARK: Tenants

private struct TenantsSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        let total = model.totalActiveCount
        VStack(alignment: .leading, spacing: 2) {
            SectionHeader(title: "Tenants", accessibilityName: "tenants", collapsed: Binding(get: { model.settings.sidebarTenantsCollapsed }, set: { model.settings.sidebarTenantsCollapsed = $0 })) {
                HStack(spacing: 4) {
                    if model.sidebarNeedsAttention {
                        Image(systemName: "exclamationmark.triangle.fill").font(.caption2).foregroundStyle(.orange)
                            .accessibilityLabel("needs attention")
                    }
                    if total > 0 { Text("\(total)").font(.caption).foregroundStyle(.green).monospacedDigit() }
                }
            }
            .padding(.horizontal, 8).padding(.bottom, 2)
            if !model.settings.sidebarTenantsCollapsed { tenantRows(total: total) }
        }
        .padding(.bottom, 8)
    }

    @ViewBuilder private func tenantRows(total: Int) -> some View {
        Group {
            SidebarRowButton(selected: model.panelSelection == .allTenants, height: 28,
                             accessibilityLabel: total > 0 ? "All tenants, \(total) active" : "All tenants",
                             action: { model.select(.allTenants) }) {
                Text("All tenants").fontWeight(.semibold).lineLimit(1)
                Spacer(minLength: 0)
                if total > 0 { Text("\(total)").foregroundStyle(model.panelSelection == .allTenants ? Color.white : Color.green).monospacedDigit() }
            }
            ForEach(model.identities) { IdentityBlock(identity: $0) }
        }
    }
}

private struct IdentityBlock: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let identity: Identity
    @State private var signingIn = false

    var body: some View {
        let collapsed = model.collapsedIdentities.contains(identity.id)
        let needsSignIn = model.needsSignIn(identity.id)
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Button { withAnimation(reduceMotion ? nil : .snappy) { model.toggleIdentity(identity.id) } } label: {
                    HStack(spacing: 3) {
                        Image(systemName: "chevron.right").rotationEffect(.degrees(collapsed ? 0 : 90))
                            .font(.system(size: 8, weight: .semibold)).frame(width: 8)
                        Text(identity.upn).font(.caption).lineLimit(1).truncationMode(.middle)
                        Spacer(minLength: 0)
                    }
                    .foregroundStyle(.secondary)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(identity.signInSummary)
                .accessibilityLabel(collapsed ? "Expand account \(identity.upn)" : "Collapse account \(identity.upn)")
                Menu { IdentityMenuItems(identity: identity) } label: { Image(systemName: "ellipsis") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    .accessibilityLabel("Account menu for \(identity.upn)")
            }
            .padding(.leading, 8).padding(.trailing, 4).padding(.top, 10)
            if needsSignIn {
                let allowed = model.isMethodAllowed(identity.signInMethod)
                SidebarRowButton(tint: .orange, accessibilityLabel: "Sign in, \(identity.upn)", action: retry) {
                    if signingIn {
                        ProgressView().controlSize(.mini)
                    } else {
                        Image(systemName: "exclamationmark.triangle").font(.caption).frame(width: 12)
                    }
                    Text("Sign in").fontWeight(.semibold).lineLimit(1)
                }
                .background(Color.orange.opacity(0.16), in: RoundedRectangle(cornerRadius: 7, style: .continuous))
                .disabled(signingIn || !allowed)
                .help(allowed ? "Sign in again as \(identity.upn)" : AppModel.disallowedMethodCaption)
            }
            if !collapsed {
                ForEach(model.tenants(for: identity.id)) { tenant in
                    TenantButton(tenant: tenant).disabled(needsSignIn).opacity(needsSignIn ? 0.5 : 1)
                }
            }
        }
    }

    private func retry() {
        guard !signingIn else { return }
        signingIn = true
        Task {
            await model.retrySignIn(identity)
            signingIn = false
        }
    }
}

private struct TenantButton: View {
    @Environment(AppModel.self) private var model
    let tenant: TenantContext

    private var error: String? { model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError }

    private var help: String {
        var lines = [tenant.displayName]
        if tenant.discoveryMode == .manualRoles { lines.append("Roles listed by hand") }
        if let r = model.entraViewOnlyReason(for: tenant.id) { lines.append(r) }
        if let r = tenant.azureUnavailableReason { lines.append(r) }
        if let r = tenant.groupsUnavailableReason { lines.append(r) }
        if let error { lines.append("Failed: \(error)") }
        return lines.joined(separator: "\n")
    }

    var body: some View {
        let mark = model.sidebarMark(for: tenant.id)
        let selected = model.panelSelection == .tenant(tenant.id)
        SidebarRowButton(selected: selected, height: 28,
                         accessibilityLabel: "\(tenant.displayName)\(markDescription(mark))",
                         action: { model.select(.tenant(tenant.id)) }) {
            Text(tenant.displayName).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 0)
            switch mark {
            case .active(let n): Text("\(n)").foregroundStyle(selected ? Color.white : Color.green).monospacedDigit()
            case .error: Image(systemName: "exclamationmark.triangle").font(.caption).foregroundStyle(selected ? Color.white : Color.red)
            case .none: EmptyView()
            }
        }
        .help(help)
    }

    private func markDescription(_ mark: TenantMark) -> String {
        switch mark {
        case .active(let n): ", \(n) active"
        case .error: ", needs attention"
        case .none: ""
        }
    }
}
