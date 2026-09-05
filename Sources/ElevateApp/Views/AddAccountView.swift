import SwiftUI
import ElevateCore

/// Picks the sign-in method for a new account. The own-app row needs a client id in Settings;
/// the two first-party rows work out of the box through the loopback browser flow.
struct AddAccountView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var method: SignInMethod?
    @State private var error: String?
    @State private var working = false

    private var methods: [SignInMethod] { model.availableMethods }
    private var selection: SignInMethod {
        method ?? methods.first { model.isAvailable($0) } ?? .azureCLI
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Add account").font(.title3.weight(.semibold))
            Picker("", selection: Binding(get: { selection }, set: { method = $0 })) {
                ForEach(methods, id: \.self) { m in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(m.displayName)
                        Text(Self.caption(for: m, available: model.isAvailable(m)))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .tag(m)
                    .disabled(!model.isAvailable(m))
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if let error { Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled) }
            HStack {
                if working { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(working)
                Button("Continue") { Task { await add() } }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(working || !model.isAvailable(selection))
            }
        }
        .padding(16).frame(width: 420)
    }

    /// SwiftUI has no per-row disabled state in a radio group, so an unavailable row says why.
    private static func caption(for method: SignInMethod, available: Bool) -> String {
        switch method {
        case .ownApp:
            available
                ? "Uses the client ID from Settings; needs admin consent in each tenant"
                : "Unavailable — configure a client ID in Settings"
        case .azureCLI:
            "Microsoft's Azure CLI app; works wherever Azure CLI is allowed; no consent needed"
        case .azurePowerShell:
            "Same, for tenants that block the Azure CLI app"
        }
    }

    private func add() async {
        working = true
        defer { working = false }
        error = nil
        let previousNotice = model.notice
        let count = model.identities.count
        let chosen = selection
        await model.addAccount(method: chosen)
        // A sign-in that added an account succeeded even if it left a notice behind (a refresh
        // token that could not be saved); anything else means the notice is the failure.
        if model.identities.count > count || model.notice == previousNotice {
            dismiss()
        } else {
            error = model.notice
            model.notice = previousNotice
        }
    }
}
