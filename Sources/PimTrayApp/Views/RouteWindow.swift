import SwiftUI
import PimTrayCore

struct RouteWindow: View {
    let route: PanelRoute

    var body: some View {
        switch route {
        case .activate(let keys): ActivationView(keys: keys)
        case .configureRoles(let tenantKey): ConfigureRolesView(tenantKey: tenantKey)
        case .addTenant(let identityId): AddTenantView(identityId: identityId)
        case .discoverTenants(let identityId): DiscoverTenantsView(identityId: identityId)
        }
    }
}

// Temporary placeholders, replaced in Task 14.
struct ActivationView: View { let keys: [RoleKey]; var body: some View { Text("Activate \(keys.count)").padding() } }
struct ConfigureRolesView: View { let tenantKey: TenantKey; var body: some View { Text("Configure").padding() } }
struct AddTenantView: View { let identityId: String; var body: some View { Text("Add tenant").padding() } }
struct DiscoverTenantsView: View { let identityId: String; var body: some View { Text("Discover").padding() } }
