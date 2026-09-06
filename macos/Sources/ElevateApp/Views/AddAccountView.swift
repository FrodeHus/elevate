import SwiftUI
import ElevateCore

/// Picks the sign-in method for a new account. The own-app row needs a client id in Settings;
/// the two first-party rows work out of the box through the loopback browser flow.
struct AddAccountView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    /// Which radio row is chosen. `custom` is a row, not a method, until a client id is typed.
    private enum Choice: Hashable { case fixed(SignInMethod), custom }

    @State private var choice: Choice?
    @State private var customClientId = ""
    @State private var error: String?
    @State private var working = false

    private var methods: [SignInMethod] { model.availableMethods }
    private var selectedChoice: Choice {
        choice ?? methods.first { model.isAvailable($0) }.map(Choice.fixed) ?? .fixed(.azureCLI)
    }
    private var selection: SignInMethod {
        switch selectedChoice {
        case .fixed(let m): m
        case .custom: .custom(clientId: customClientId.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Add account").font(.title3.weight(.semibold))
            Picker("", selection: Binding(get: { selectedChoice }, set: { choice = $0 })) {
                ForEach(methods, id: \.self) { m in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(m.displayName)
                        Text(Self.caption(for: m, available: model.isAvailable(m), viaLoopback: model.ownAppViaLoopback))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .tag(Choice.fixed(m))
                    .disabled(!model.isAvailable(m))
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text("Custom app")
                    Text("An Entra app registration without a macOS platform, e.g. your company's PIM app; signs in through the browser with the standard http://localhost loopback redirect")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .tag(Choice.custom)
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if selectedChoice == .custom {
                TextField("Application (client) ID", text: $customClientId)
                    .textFieldStyle(.roundedBorder)
                    .font(.body.monospaced())
                    .padding(.leading, 20)
                if !customClientId.isEmpty, !model.isAvailable(selection) {
                    Text("Enter the application (client) ID as a GUID").font(.caption).foregroundStyle(.orange).padding(.leading, 20)
                }
            }
            limitations
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
        .padding(16).frame(width: 440)
        .onAppear { customClientId = model.rememberedCustomClientId }
    }

    /// What the chosen method can and cannot do, stated before the account is added.
    @ViewBuilder private var limitations: some View {
        if let summary = selection.limitationSummary {
            VStack(alignment: .leading, spacing: 4) {
                Label(summary, systemImage: "exclamationmark.triangle.fill").font(.callout.weight(.medium))
                Text("Microsoft grants the \(selection.displayName) no Graph PIM permissions, so Elevate skips Entra directory roles for this account entirely. Azure resource roles are discovered, activated and deactivated normally. Use your own or a custom app registration for Entra roles.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        } else if selection.isCustom {
            VStack(alignment: .leading, spacing: 4) {
                Label("Capabilities depend on what the app was consented for.", systemImage: "info.circle")
                    .font(.callout.weight(.medium))
                Text("The registration needs http://localhost as a redirect URI under the Mobile and desktop applications platform (no secret is used; the \"Allow public client flows\" toggle is not required). Elevate reads the granted scopes from the token after sign-in: if RoleAssignmentSchedule.ReadWrite.Directory is missing, the account is marked as supporting Azure resource roles only; those need only ARM user_impersonation.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.blue.opacity(0.10), in: RoundedRectangle(cornerRadius: 8))
        } else {
            Label("Entra and Azure resource roles: activate and deactivate.", systemImage: "checkmark.circle")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    /// An unavailable row explains why, since its `.disabled` state alone is easy to miss.
    private static func caption(for method: SignInMethod, available: Bool, viaLoopback: Bool) -> String {
        switch method {
        case .ownApp:
            if available && viaLoopback {
                "Uses the client ID from Settings through the browser (loopback) on this unsigned build; the registration needs http://localhost under Mobile and desktop applications"
            } else if available {
                "Uses the client ID from Settings; needs admin consent in each tenant"
            } else {
                "Unavailable — configure a client ID in Settings"
            }
        case .azureCLI:
            "Microsoft's Azure CLI app; no consent needed; Azure resource roles only"
        case .azurePowerShell:
            "Azure resource roles only; for tenants that block the Azure CLI app"
        case .custom:
            "An Entra app registration without a macOS platform, used through the loopback flow"
        }
    }

    private func add() async {
        working = true
        defer { working = false }
        error = nil
        let previousNotice = model.notice
        let chosen = selection
        let added = await model.addAccount(method: chosen)
        if added {
            dismiss()
        } else {
            error = model.notice
            model.notice = previousNotice
        }
    }
}
