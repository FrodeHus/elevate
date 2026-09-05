import SwiftUI
import ElevateCore

struct RouteWindow: View {
    let route: PanelRoute

    var body: some View {
        switch route {
        case .activate(let keys): ActivationView(keys: keys)
        case .configureRoles(let tenantKey): ConfigureRolesView(tenantKey: tenantKey)
        case .addTenant(let identityId): AddTenantView(identityId: identityId)
        case .discoverTenants(let identityId): DiscoverTenantsView(identityId: identityId)
        case .addAccount: AddAccountView()
        case .saveProfile(let keys): SaveProfileView(keys: keys)
        case .runProfile(let id): RunProfileView(profileId: id)
        case .manageProfiles: ManageProfilesView()
        case .decide(let requestId, let approve): DecisionView(requestId: requestId, approve: approve)
        }
    }
}
