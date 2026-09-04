import SwiftUI
import PimTrayCore

struct IdentitySection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let identity: Identity

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Image(systemName: "person.crop.circle")
                Text(identity.upn).font(.subheadline.weight(.semibold))
                Spacer()
                Menu {
                    Button("Discover tenants…") { open(.discoverTenants(identity.id)) }
                    Button("Add tenant…") { open(.addTenant(identity.id)) }
                    Divider()
                    Button("Sign out", role: .destructive) { model.signOut(identity) }
                } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }
            ForEach(model.tenants(for: identity.id)) { tenant in
                TenantSection(tenant: tenant)
            }
        }
        .padding(8)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
