import SwiftUI
import ElevateCore

struct SaveProfileView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let keys: [RoleKey]
    @State private var name = ""

    private var grouped: [(TenantKey, [RoleKey])] {
        var order: [TenantKey] = []; var map: [TenantKey: [RoleKey]] = [:]
        for k in keys { if map[k.tenantKey] == nil { order.append(k.tenantKey) }; map[k.tenantKey, default: []].append(k) }
        return order.map { ($0, map[$0]!) }
    }

    /// What running the profile would ask for today: the remembered duration, else the policy
    /// default, never above the maximum. Shown for every row so an empty one is not a surprise.
    private func resolvedDuration(for k: RoleKey) -> Duration {
        let policy = model.role(for: k)?.policy ?? .manualDefault
        return min(model.remembered(for: k)?.lastDuration ?? policy.defaultDuration, policy.maximumDuration)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Save as profile").font(.title3.weight(.semibold))
            TextField("Profile name", text: $name).textFieldStyle(.roundedBorder)
            VStack(alignment: .leading, spacing: 4) {
                ForEach(grouped, id: \.0) { tk, tkeys in
                    Text("\(model.identity(tk.identityId)?.upn ?? tk.identityId) · \(model.tenant(tk)?.displayName ?? tk.tenantId)")
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                    ForEach(tkeys, id: \.self) { k in
                        HStack {
                            Text(model.summaryName(for: k))
                            Spacer()
                            Text(Countdown.label(resolvedDuration(for: k))).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .padding(8).background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") { model.saveProfile(name: name, keys: keys); model.selectMode = false; dismiss() }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(16).frame(width: 420)
    }
}
