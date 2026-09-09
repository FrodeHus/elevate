import SwiftUI
import ElevateCore

struct AddTenantView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let identityId: String
    @State private var input = ""
    @State private var error: String?
    @State private var working = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField("Tenant id or verified domain (e.g. fabrikam.com)", text: $input)
            if let error { Text(error).font(.caption).foregroundStyle(.red) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Add") { Task { await add() } }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(working || input.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(16).frame(width: 420)
        .navigationTitle("Add tenant for \(model.identity(identityId)?.upn ?? identityId)")
    }

    private func add() async {
        working = true
        defer { working = false }
        do {
            try await model.addTenant(identityId: identityId, domainOrId: input)
            dismiss()
        } catch {
            self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }
}

struct DiscoverTenantsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let identityId: String
    @State private var found: [DiscoveredTenant] = []
    @State private var chosen: Set<String> = []
    @State private var error: String?
    @State private var loading = true

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if loading { ProgressView("Asking Azure Resource Manager…") }
            else if let error { Text(error).font(.caption).foregroundStyle(.red) }
            else {
                List(found) { t in
                    let tracked = model.tenant(TenantKey(identityId: identityId, tenantId: t.tenantId)) != nil
                    // A tenant the organization does not allow cannot be tracked; the row says why
                    // instead of quietly doing nothing when it is ticked.
                    let allowed = model.isTenantAllowed(t.tenantId)
                    Toggle(isOn: Binding(get: { allowed && (tracked || chosen.contains(t.tenantId)) },
                                         set: { on in if on { chosen.insert(t.tenantId) } else { chosen.remove(t.tenantId) } })) {
                        VStack(alignment: .leading) {
                            Text(t.displayName)
                            Text(allowed ? (t.defaultDomain ?? t.tenantId) : "Not permitted by your organization")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .disabled(tracked || !allowed)
                }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Track selected") { Task { await track() } }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(chosen.isEmpty)
            }
        }
        .padding(16).frame(width: 460, height: 380)
        .navigationTitle("Tenants for \(model.identity(identityId)?.upn ?? identityId)")
        .task {
            do { found = try await model.discoverTenants(identityId: identityId) }
            catch { self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription }
            loading = false
        }
    }

    private func track() async {
        await model.trackTenants(identityId: identityId, tenants: found.filter { chosen.contains($0.tenantId) })
        dismiss()
    }
}
