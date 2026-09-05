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
                if identity.signInMethod != .ownApp {
                    Text(identity.signInMethod.displayName).font(.caption2).foregroundStyle(.secondary)
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
        .background(Color.accentColor.opacity(0.14))
        .overlay(alignment: .bottom) { Rectangle().fill(Color.accentColor.opacity(0.35)).frame(height: 1) }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
