import SwiftUI
import ElevateCore

/// Picks eligible roles from every account and tenant to add to a profile. Roles the profile
/// already holds are listed but disabled, so a search that finds nothing new says why.
struct AddRolesPicker: View {
    @Environment(AppModel.self) private var model
    let profile: ActivationProfile
    let onAdd: ([RoleKey]) -> Void
    @State private var query = ""
    @State private var kind: RoleScopeKind?
    @State private var picked: Set<RoleKey> = []
    @FocusState private var searchFocused: Bool

    private var inProfile: Set<RoleKey> { Set(profile.entries.map(\.roleKey)) }

    /// Tenants in panel order, each with the roles that pass the kind and search filters.
    private struct Group: Identifiable {
        let tenant: TenantContext
        let roles: [EligibleRole]
        var id: TenantKey { tenant.id }
    }

    private var groups: [Group] {
        var out: [Group] = []
        for identity in model.identities {
            for tenant in model.tenants(for: identity.id) {
                let roles = model.roles(for: tenant.id)
                    .filter { kind == nil || $0.key.scope.kind == kind }
                    .filter { PanelFilter.matches(query: query, role: $0, tenantName: tenant.displayName, upn: identity.upn) }
                    .sorted { ($0.key.scope.kind.rawValue, $0.displayName) < ($1.key.scope.kind.rawValue, $1.displayName) }
                if !roles.isEmpty || model.busy.contains(tenant.id) { out.append(Group(tenant: tenant, roles: roles)) }
            }
        }
        return out
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(spacing: 8) {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Search roles", text: $query).textFieldStyle(.plain).focused($searchFocused)
                }
                Picker("", selection: $kind) {
                    Text("All").tag(RoleScopeKind?.none)
                    Text("Entra").tag(RoleScopeKind?.some(.entraDirectory))
                    Text("Azure").tag(RoleScopeKind?.some(.azureResource))
                    Text("Groups").tag(RoleScopeKind?.some(.group))
                }
                .pickerStyle(.segmented).labelsHidden().controlSize(.small)
            }
            .padding(10)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    let groups = groups
                    if groups.isEmpty {
                        Text(query.isEmpty ? "No eligible roles loaded yet. Refresh the panel and try again." : "No matches")
                            .font(.caption).foregroundStyle(.secondary).padding(10)
                    }
                    ForEach(groups) { group in
                        TenantGroup(tenantKey: group.tenant.id) {
                            if model.busy.contains(group.tenant.id) {
                                HStack(spacing: 6) { ProgressView().controlSize(.mini); Text("loading…").font(.caption).foregroundStyle(.secondary) }
                            }
                            ForEach(group.roles) { role in row(role) }
                        }
                    }
                }
                .padding(8)
            }
            .frame(height: 300)
            Divider()
            HStack {
                Text("Eligible roles across all accounts.").font(.caption2).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { onAdd([]) }.keyboardShortcut(.cancelAction)
                Button("Add \(picked.count)") { onAdd(Array(picked)) }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(picked.isEmpty)
            }
            .padding(10)
        }
        .frame(width: 400)
        .onAppear { searchFocused = true }
    }

    private func row(_ role: EligibleRole) -> some View {
        let key = role.key
        let already = inProfile.contains(key)
        let viewOnly = key.scope.kind == .entraDirectory ? model.entraViewOnlyReason(for: key.tenantKey) : nil
        return HStack(spacing: 8) {
            Toggle(isOn: Binding(get: { already || picked.contains(key) },
                                 set: { on in if on { picked.insert(key) } else { picked.remove(key) } })) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(role.displayName).lineLimit(1)
                    if let detail = role.detail { Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1) }
                }
            }
            .disabled(already || viewOnly != nil)
            .help(viewOnly ?? "")
            Spacer()
            Text(already ? "in profile" : trailing(role)).font(.caption2).foregroundStyle(.secondary)
        }
        .opacity(already ? 0.6 : 1)
    }

    private func trailing(_ role: EligibleRole) -> String {
        let kindName: String = switch role.key.scope.kind { case .entraDirectory: "Entra"; case .azureResource: "Azure"; case .group: "Group" }
        if let notes = PolicyNotes.caption(for: role.policy) { return "\(kindName) · \(notes)" }
        return kindName
    }
}
