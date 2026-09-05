import SwiftUI
import ElevateCore

/// Full account row shown between accounts. Accent-tinted so accounts read differently from tenants.
struct IdentityHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let identity: Identity

    var body: some View {
        let expanded = !model.collapsedIdentities.contains(identity.id)
        HStack(spacing: 8) {
            Button { withAnimation(.snappy) { model.toggleIdentity(identity.id) } } label: {
                Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                    .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
            }
            .buttonStyle(.plain).accessibilityLabel(expanded ? "Collapse account" : "Expand account")
            Image(systemName: "person.crop.circle.fill").foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 0) {
                Text(identity.upn).font(.subheadline.weight(.semibold)).lineLimit(1).truncationMode(.middle)
                    .contentShape(Rectangle()).onTapGesture { withAnimation(.snappy) { model.toggleIdentity(identity.id) } }
                HStack(spacing: 6) {
                    if identity.signInMethod != .ownApp {
                        Text(identity.signInMethod.displayName).font(.caption2).foregroundStyle(.secondary)
                    }
                    // Account-level badge follows the tenants: it disappears once every tenant's token proves the write scope.
                    if let reason = model.tenants(for: identity.id).lazy.compactMap({ model.entraViewOnlyReason(for: $0.id) }).first
                        ?? (model.tenants(for: identity.id).isEmpty ? identity.signInMethod.entraViewOnlyReason : nil) {
                        ViewOnlyBadge(reason: reason)
                    }
                }
            }
            Spacer(minLength: 8)
            Menu {
                Button("Discover tenants…") { open(.discoverTenants(identity.id)) }
                Button("Add tenant…") { open(.addTenant(identity.id)) }
                Divider()
                Button("Sign out", role: .destructive) { model.signOut(identity) }
            } label: { Image(systemName: "ellipsis.circle") }
            .menuStyle(.borderlessButton).fixedSize()
            .accessibilityLabel("Account actions")
        }
        .padding(.horizontal, PanelMetrics.headerInset)
        .padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        // A neutral fill keeps the primary text at full contrast in both appearances; the accent
        // survives as the glyph and a left edge, which is enough to tell accounts from tenants.
        .background(.quaternary.opacity(0.5))
        .overlay(alignment: .leading) { Rectangle().fill(Color.accentColor).frame(width: 3) }
        .overlay(alignment: .bottom) { Divider() }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}

/// Small tinted capsule for a status the user should notice but not read at length; the full
/// text is the tooltip. Used for "Azure roles only" and for a failed discovery.
struct StatusPill: View {
    let text: String
    var tint: Color = .orange
    let help: String
    var body: some View {
        Text(text).font(.caption2.weight(.medium))
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(tint.opacity(0.2), in: Capsule())
            .foregroundStyle(.primary)
            .help(help)
            .accessibilityLabel("\(text): \(help)")
    }
}

/// "Azure roles only" pill, same on account headers and tenant headers.
struct ViewOnlyBadge: View {
    let reason: String
    var body: some View { StatusPill(text: "Azure roles only", help: reason) }
}
