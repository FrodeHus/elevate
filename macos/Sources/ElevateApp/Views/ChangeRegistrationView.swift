import SwiftUI
import ElevateCore

/// Moves an account onto an Entra app registration — the Settings one or one of its own. For an
/// account already on an Entra app registration this switches it between the two; for an Azure
/// CLI / Azure PowerShell / other-app account it upgrades it, so Elevate can also read and
/// activate Entra roles and PIM for Groups for it. The switch is saved only after the same
/// account signs in with the new registration.
struct ChangeRegistrationView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let identityId: String

    private enum Registration: Hashable { case settings, different }

    @State private var registration: Registration?
    @State private var clientId = ""
    @State private var error: String?
    @State private var working = false

    private var identity: Identity? { model.identity(identityId) }
    private var isUpgrade: Bool { identity?.signInMethod.isOwnApp == false }
    private var chosen: Registration {
        if let registration { return registration }
        // Under a managed client id only the Settings (managed) registration can be chosen.
        if identity?.signInMethod.isPinned == true, model.canPin { return .different }
        if model.isAvailable(.ownApp) { return .settings }
        return model.canPin ? .different : .settings
    }
    private var target: SignInMethod { chosen == .settings ? .ownApp : .pinned(clientId) }
    private var canContinue: Bool {
        guard let identity, !working else { return false }
        return target != identity.signInMethod && model.isAvailable(target) && !model.isAccountBusy(identityId)
    }
    private var displayedTitle: String {
        guard let identity else { return "App registration for \(identityId)" }
        return isUpgrade ? "Upgrade \(identity.upn) to an Entra app registration" : "App registration for \(identity.upn)"
    }
    private var introText: String {
        guard let identity else { return "" }
        if isUpgrade {
            return "\(identity.upn) signs in with the \(identity.signInMethod.displayName) today. With an Entra app registration Elevate also reads and activates Entra roles and PIM for Groups. Elevate signs the account in with the registration you choose and keeps its tenants, roles and profiles. Nothing changes if the sign-in is cancelled or a different account signs in."
        }
        return "Elevate signs \(identity.upn) in with the registration you choose and keeps its tenants, roles and profiles. Nothing changes if the sign-in is cancelled or a different account signs in."
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(introText)
                .font(.callout).fixedSize(horizontal: false, vertical: true)
            Picker("", selection: Binding(get: { chosen }, set: { registration = $0 })) {
                Text("Follow the registration in Settings (\(model.settingsRegistrationLabel))")
                    .tag(Registration.settings)
                    .disabled(!model.isAvailable(.ownApp))
                if model.canPin {
                    Text("Use a different registration").tag(Registration.different)
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if chosen == .different {
                TextField("Application (client) ID", text: $clientId)
                    .textFieldStyle(.roundedBorder)
                    .font(.body.monospaced())
                    .padding(.leading, 20)
                if !clientId.isEmpty, !AppSettings.isValidClientId(clientId) {
                    Text("Enter the application (client) ID as a GUID").font(.caption).foregroundStyle(.orange).padding(.leading, 20)
                }
            }
            if model.isAccountBusy(identityId) {
                Text("Wait for this account's requests to finish.").font(.caption).foregroundStyle(.orange)
            }
            if let error { Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled) }
            HStack {
                if working { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(working)
                Button("Sign in and switch") { Task { await apply() } }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(!canContinue)
            }
        }
        .padding(16).frame(width: 440)
        .navigationTitle(displayedTitle)
        .onAppear {
            if case .pinnedApp(let id) = identity?.signInMethod { clientId = id }
            else { clientId = model.rememberedPinnedClientId }
        }
    }

    private func apply() async {
        guard let identity else { return }
        working = true
        defer { working = false }
        error = nil
        let previousNotice = model.notice
        if await model.changeSignInRegistration(identity, to: target) {
            dismiss()
        } else {
            error = model.notice
            model.notice = previousNotice
        }
    }
}
