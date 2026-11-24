import SwiftUI
import AppKit
import ElevateCore

enum PanelMetrics {
    static let sidebarWidth: CGFloat = 136
    static let mainWidth: CGFloat = 380
    static let width: CGFloat = sidebarWidth + mainWidth
    static let maxHeight: CGFloat = 720
    static let headerInset: CGFloat = 12      // account row, tenant header
    static let roleInset: CGFloat = 28        // role rows: status-dot column
    static let trailingInset: CGFloat = 12
    static let countdownWidth: CGFloat = 44
}

struct PanelView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var topHeight: CGFloat = 0
    @State private var bottomHeight: CGFloat = 0
    @FocusState private var searchFocused: Bool

    var body: some View {
        HStack(spacing: 0) {
            PanelSidebar().frame(width: PanelMetrics.sidebarWidth)
            mainColumn.frame(width: PanelMetrics.mainWidth)
        }
        .frame(width: PanelMetrics.width)
        .onAppear { searchFocused = true }
    }

    private var mainColumn: some View {
        @Bindable var model = model
        return VStack(spacing: 0) {
            VStack(spacing: 0) {
                PanelHeader()
                Divider()
                // The confirmation cards for the sidebar's menus render here: the sidebar is too narrow for them.
                Color.clear.frame(height: 0)
                    .rowLevelProfileDeleteConfirmation(pinned: model.pinnedProfiles, suppressedByPopover: model.allProfilesOpen)
                    .signOutConfirmation()
                // Always there while there is a list to filter, and focused when the panel opens.
                if !model.identities.isEmpty {
                    HStack(spacing: 6) {
                        // The field is always shown, so no search button (as on Windows); ⌘F still jumps back to it.
                        Button("") { searchFocused = true }
                            .keyboardShortcut("f")
                            .frame(width: 0, height: 0)
                            .opacity(0)
                            .accessibilityHidden(true)
                        Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                        TextField(filterPlaceholder, text: Binding(get: { model.searchQuery }, set: { model.searchQuery = $0 }))
                            .textFieldStyle(.plain)
                            .focused($searchFocused)
                            .onExitCommand { model.handleEscape() }
                            // The keyboard stays in the field; the arrows move a highlight through the rows.
                            .onKeyPress(.downArrow) { model.moveHighlight(1); return .handled }
                            .onKeyPress(.upArrow) { model.moveHighlight(-1); return .handled }
                            .onSubmit {
                                guard let key = model.returnTarget else { return }
                                if NSEvent.modifierFlags.contains(.option) {
                                    Task { if await !model.quickActivate(key) { open(.activate([key])) } }
                                } else { open(.activate([key])) }
                            }
                        if !model.searchQuery.isEmpty {
                            Button { model.searchQuery = "" } label: { Image(systemName: "xmark.circle.fill") }
                                .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("Clear filter")
                        }
                    }
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    Divider()
                }
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
                if let update = model.updateAvailable {
                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: "arrow.down.circle").foregroundStyle(.blue)
                        Text("Elevate \(update.version) is available").font(.caption)
                        Spacer()
                        Button("Open") { NSWorkspace.shared.open(update.url) }
                            .buttonStyle(.borderless)
                        Button("Dismiss") { model.dismissUpdate() }
                            .buttonStyle(.borderless)
                    }
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(.blue.opacity(0.12))
                    Divider()
                }
                // After an Azure or group activation: cached az / kubelogin tokens predate it. Dismiss hides it for that account.
                if let hint = model.tokenHint {
                    // The text wraps to as many lines as it needs: without fixedSize the HStack sizes the
                    // row for one line and the rest of the caption spills over the list below it.
                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: "key.horizontal").foregroundStyle(.blue)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(TokenCacheHint.message(account: hint.account))
                                .font(.caption).textSelection(.enabled)
                                .fixedSize(horizontal: false, vertical: true)
                            Text(TokenCacheHint.advice)
                                .font(.caption2).foregroundStyle(.secondary).textSelection(.enabled)
                                .fixedSize(horizontal: false, vertical: true)
                            HStack(spacing: 8) {
                                Button("Copy command") {
                                    NSPasteboard.general.clearContents()
                                    NSPasteboard.general.setString(TokenCacheHint.azureCliCommand, forType: .string)
                                }
                                .buttonStyle(.borderless).help("Copies '\(TokenCacheHint.azureCliCommand)'")
                                Button("Dismiss") { model.dismissTokenHint() }
                                    .buttonStyle(.borderless).help("Hide this for \(hint.account)")
                            }
                            .font(.caption)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(.blue.opacity(0.12))
                    Divider()
                }
            }
            .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { topHeight = $0 }
            if !model.isConfigured && model.identities.isEmpty {
                SetupView()
            } else if let fatal = model.startupError {
                ContentUnavailableView("Elevate cannot start", systemImage: "exclamationmark.triangle", description: Text(fatal))
                    .frame(height: 200)
            } else if model.identities.isEmpty {
                ContentUnavailableView {
                    Label("No accounts", systemImage: "person.crop.circle.badge.plus")
                } description: {
                    Text(model.usesSharedApp
                         ? "Configured with the shared Elevate app. Ask an administrator to grant consent once per tenant, then add an account."
                         : "Add an account to see your PIM roles.")
                } actions: {
                    if model.usesSharedApp {
                        Button {
                            openWindow(value: PanelRoute.addAccount)
                            NSApp.activate(ignoringOtherApps: true)
                        } label: { Text("Add account…") }
                            .buttonStyle(.borderedProminent)
                        if let consent = model.sharedAppAdminConsentURL() {
                            Button("Grant admin consent…") { NSWorkspace.shared.open(consent) }
                        }
                    }
                }
                .frame(height: model.usesSharedApp ? 220 : 160)
            } else {
                switch model.panelContent {
                case .tenant(let key): RoleListView(tenant: key, availableHeight: listHeight)
                // Home and the profile view arrive with their own tasks; until then they show the list.
                case .roleList, .home, .profile: RoleListView(tenant: nil, availableHeight: listHeight)
                }
            }
            Spacer(minLength: 0)
            VStack(spacing: 0) {
                if model.selectMode {
                    Divider()
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(spacing: 8) {
                            Button("Save as profile…") { open(.saveProfile(Array(model.selection).sorted { "\($0)" < "\($1)" })) }
                                .disabled(model.selection.isEmpty)
                            Button {
                                open(.activate(Array(model.selection).sorted { "\($0)" < "\($1)" }))
                            } label: {
                                Text("Activate \(model.selectionCount) \(model.selectionNoun)\(model.selectionCount == 1 ? "" : "s")").frame(maxWidth: .infinity)
                            }
                            .buttonStyle(.borderedProminent)
                            .disabled(model.selection.isEmpty)
                        }
                        if let hint = selectionHint {
                            Text(hint).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    .padding(10)
                }
                Divider()
                footer
            }
            .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { bottomHeight = $0 }
        }
    }

    /// One line under the bulk bar naming what is picked per tab, so a selection spanning tabs is
    /// visible from whichever tab is showing. Nil when nothing is selected.
    private var selectionHint: String? {
        let b = model.selectionBreakdown
        var parts: [String] = []
        if b.entra > 0 { parts.append("\(b.entra) Entra") }
        if b.azure > 0 { parts.append("\(b.azure) Azure") }
        if b.groups > 0 { parts.append("\(b.groups) group\(b.groups == 1 ? "" : "s")") }
        guard !parts.isEmpty else { return nil }
        return "\(model.selectionCount) selected · \(parts.joined(separator: ", ")) — switch tabs to add more"
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }

    private var listHeight: CGFloat { max(PanelMetrics.maxHeight - topHeight - bottomHeight, 44) }

    private var filterPlaceholder: LocalizedStringKey {
        if case .tenant(let key) = model.panelContent, let name = model.tenant(key)?.displayName { return "Filter roles in \(name)" }
        return "Filter roles"
    }

    private var footer: some View {
        HStack {
            SettingsLink { Text("Settings…") }.buttonStyle(.borderless).keyboardShortcut(",")
            Spacer()
            Button("Quit") { NSApp.terminate(nil) }.keyboardShortcut("q")
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}

/// A tenant's pinned header plus its role rows, as one `Section` so the header sticks while its rows scroll.
struct TenantBlock: View {
    @Environment(AppModel.self) private var model
    let identity: Identity
    let tenant: TenantContext

    var body: some View {
        Section {
            if !model.collapsedTenants.contains(tenant.id) { TenantRoles(tenant: tenant) }
        } header: {
            TenantHeader(tenant: tenant, identity: identity)
        }
    }
}
