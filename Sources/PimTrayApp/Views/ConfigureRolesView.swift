import SwiftUI
import PimTrayCore

struct ConfigureRolesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tenantKey: TenantKey

    @State private var catalogue: [CatalogueRole] = []
    @State private var search = ""
    @State private var selectedEntra: Set<String> = []          // template ids
    @State private var azure: [AzureRow] = []
    @State private var groups: [GroupRow] = []

    struct AzureRow: Identifiable { let id = UUID(); var scope = ""; var roleName = "Contributor" }
    struct GroupRow: Identifiable { let id = UUID(); var groupId = ""; var displayName = ""; var access: GroupAccess = .member }

    static let azureRoleNames = ["Owner", "Contributor", "Reader", "User Access Administrator", "Key Vault Administrator", "Storage Blob Data Contributor"]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Known PIM roles in \(model.tenant(tenantKey)?.displayName ?? tenantKey.tenantId)").font(.title3.weight(.semibold))
            Text("Roles you believe you are eligible for. Activation is still validated by Entra.").font(.caption).foregroundStyle(.secondary)
            TabView {
                entraTab.tabItem { Text("Entra roles") }
                azureTab.tabItem { Text("Azure resources") }
                groupsTab.tabItem { Text("Groups") }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") { save() }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
            }
        }
        .padding(16)
        .frame(width: 560, height: 520)
        .onAppear(perform: load)
    }

    private var entraTab: some View {
        VStack {
            TextField("Search roles", text: $search)
            List(filtered) { role in
                Toggle(isOn: Binding(get: { selectedEntra.contains(role.templateId) },
                                     set: { on in if on { selectedEntra.insert(role.templateId) } else { selectedEntra.remove(role.templateId) } })) {
                    VStack(alignment: .leading) {
                        HStack { Text(role.displayName); if role.isPrivileged { Text("privileged").font(.caption2).foregroundStyle(.orange) } }
                        Text(role.description).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                }
            }
        }
    }

    private var filtered: [CatalogueRole] {
        search.isEmpty ? catalogue : catalogue.filter { $0.displayName.localizedCaseInsensitiveContains(search) }
    }

    private var azureTab: some View {
        VStack(alignment: .leading) {
            Text("Scope is the full resource id, e.g. /subscriptions/<id> or /subscriptions/<id>/resourceGroups/<name>.").font(.caption).foregroundStyle(.secondary)
            List($azure) { $row in
                HStack {
                    TextField("Scope", text: $row.scope)
                    Picker("", selection: $row.roleName) {
                        ForEach(Self.azureRoleNames, id: \.self) { Text($0).tag($0) }
                    }.frame(width: 220)
                    Button { azure.removeAll { $0.id == row.id } } label: { Image(systemName: "minus.circle") }.buttonStyle(.borderless)
                }
            }
            Button("Add row") { azure.append(AzureRow()) }
            Text("Activation for Azure resource roles arrives in phase 2.").font(.caption2).foregroundStyle(.secondary)
        }
    }

    private var groupsTab: some View {
        VStack(alignment: .leading) {
            List($groups) { $row in
                HStack {
                    TextField("Group id", text: $row.groupId)
                    TextField("Display name", text: $row.displayName)
                    Picker("", selection: $row.access) { Text("Member").tag(GroupAccess.member); Text("Owner").tag(GroupAccess.owner) }.frame(width: 110)
                    Button { groups.removeAll { $0.id == row.id } } label: { Image(systemName: "minus.circle") }.buttonStyle(.borderless)
                }
            }
            Button("Add row") { groups.append(GroupRow()) }
            Text("Activation for PIM for Groups arrives in phase 3.").font(.caption2).foregroundStyle(.secondary)
        }
    }

    private func load() {
        catalogue = (try? RoleCatalogue.entraBuiltInRoles()) ?? []
        for m in model.manualRoles(for: tenantKey) {
            switch m.scope {
            case .entraDirectory(let id, _): selectedEntra.insert(id)
            case .azureResource(let scope, let roleDefinitionId): azure.append(AzureRow(scope: scope, roleName: roleDefinitionId))
            case .group(let gid, let access): groups.append(GroupRow(groupId: gid, displayName: m.displayName, access: access))
            }
        }
    }

    private func save() {
        var manual: [ManualRole] = []
        for role in catalogue where selectedEntra.contains(role.templateId) {
            manual.append(ManualRole(tenantKey: tenantKey, scope: .entraDirectory(roleDefinitionId: role.templateId, directoryScopeId: "/"), displayName: role.displayName))
        }
        for row in azure where !row.scope.trimmingCharacters(in: .whitespaces).isEmpty {
            manual.append(ManualRole(tenantKey: tenantKey, scope: .azureResource(scope: row.scope.trimmingCharacters(in: .whitespaces), roleDefinitionId: row.roleName), displayName: "\(row.roleName) · \(row.scope)"))
        }
        for row in groups where !row.groupId.trimmingCharacters(in: .whitespaces).isEmpty {
            manual.append(ManualRole(tenantKey: tenantKey, scope: .group(groupId: row.groupId.trimmingCharacters(in: .whitespaces), accessId: row.access),
                                     displayName: row.displayName.isEmpty ? row.groupId : row.displayName))
        }
        model.setManualRoles(manual, for: tenantKey)
        dismiss()
    }
}
