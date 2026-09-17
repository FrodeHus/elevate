import SwiftUI
import ElevateCore

/// Picks the sign-in method for a new account. The Entra app registration row uses the Settings
/// registration or one the account keeps for itself; the two first-party rows work out of the
/// box through the loopback browser flow.
struct AddAccountView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    /// Which radio row is chosen. `custom` is a row, not a method, until a client id is typed.
    private enum Choice: Hashable { case fixed(SignInMethod), custom }
    /// Which registration the "Entra app registration" row uses.
    private enum Registration: Hashable { case settings, different }

    @State private var choice: Choice?
    @State private var registration: Registration?
    @State private var customClientId = ""
    @State private var pinnedClientId = ""
    @State private var error: String?
    @State private var working = false

    init() {}

    /// Preselects "Use a different registration" under the Entra row. Used only by previews and
    /// the offscreen visual-check test, since `@State` cannot otherwise be set from outside.
    init(startWithDifferentRegistration: Bool) {
        _registration = State(initialValue: startWithDifferentRegistration ? .different : .settings)
    }

    private var methods: [SignInMethod] { model.availableMethods }
    /// The Entra row is usable through either registration.
    private func rowEnabled(_ m: SignInMethod) -> Bool {
        m == .ownApp ? (model.isAvailable(.ownApp) || model.canPin) : model.isAvailable(m)
    }
    private var selectedChoice: Choice {
        // With every fixed method withheld by the organization, "Company app" is all that is left
        // — and it may be withheld too, in which case the dialog has nothing to offer.
        choice ?? methods.first { rowEnabled($0) }.map(Choice.fixed) ?? methods.first.map(Choice.fixed) ?? .custom
    }
    private var selectedRegistration: Registration {
        registration ?? (model.isAvailable(.ownApp) || !model.canPin ? .settings : .different)
    }
    private var selection: SignInMethod {
        switch selectedChoice {
        case .fixed(.ownApp) where selectedRegistration == .different: .pinned(pinnedClientId)
        case .fixed(let m): m
        case .custom: .custom(clientId: customClientId.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if methods.contains(.ownApp) {
                methodPicker([.ownApp], includeCustom: false)
                if selectedChoice == .fixed(.ownApp) { registrationOptions.padding(.leading, 20) }
            }
            methodPicker(methods.filter { $0 != .ownApp }, includeCustom: model.isCustomMethodAllowed)
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
        .padding(16).frame(width: 460)
        .navigationTitle("Add account")
        .onAppear {
            customClientId = model.rememberedCustomClientId
            pinnedClientId = model.rememberedPinnedClientId
        }
    }

    @ViewBuilder
    private func methodPicker(_ rows: [SignInMethod], includeCustom: Bool) -> some View {
        if !rows.isEmpty || includeCustom {
            Picker("", selection: Binding(get: { selectedChoice }, set: { choice = $0 })) {
                ForEach(rows, id: \.self) { m in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(m.displayName)
                        Text(caption(for: m)).font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .tag(Choice.fixed(m))
                    .disabled(!rowEnabled(m))
                }
                if includeCustom {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Company app (client ID)")
                        Text("A registration that lists only http://localhost, such as an existing company PIM app; signs in through the browser")
                            .font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .tag(Choice.custom)
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
        }
    }

    @ViewBuilder private var registrationOptions: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker("", selection: Binding(get: { selectedRegistration }, set: { registration = $0 })) {
                Text("Use the registration in Settings (\(model.settingsRegistrationLabel))")
                    .tag(Registration.settings)
                    .disabled(!model.isAvailable(.ownApp))
                if model.canPin {
                    Text("Use a different registration").tag(Registration.different)
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if selectedRegistration == .different {
                VStack(alignment: .leading, spacing: 4) {
                    TextField("Application (client) ID", text: $pinnedClientId)
                        .textFieldStyle(.roundedBorder)
                        .font(.body.monospaced())
                    if !pinnedClientId.isEmpty, !model.isAvailable(selection) {
                        Text("Enter the application (client) ID as a GUID").font(.caption).foregroundStyle(.orange)
                    } else if model.matchesSettingsClientId(pinnedClientId) {
                        Text("Matches the registration in Settings; this account keeps this ID even if Settings changes.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Text(model.ownAppViaLoopback
                         ? "Needs the same setup as the Elevate app: http://localhost under Mobile and desktop applications on this unsigned build, the Graph PIM scopes, and admin consent."
                         : "Needs the same setup as the Elevate app: redirect \(AppSettings.redirectUri) under Mobile and desktop applications, the Graph PIM scopes, and admin consent.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                .padding(.leading, 20)
            }
        }
    }

    /// What the chosen method can and cannot do, stated before the account is added.
    @ViewBuilder private var limitations: some View {
        if let summary = selection.limitationSummary {
            VStack(alignment: .leading, spacing: 4) {
                Label(summary, systemImage: "info.circle").font(.callout.weight(.medium))
                Text("Microsoft grants the \(selection.displayName) no Graph PIM permissions, so Elevate skips Entra directory roles for this account entirely. Azure resource roles are discovered, activated and deactivated normally. Use your own or a custom app registration for Entra roles.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
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
    private func caption(for method: SignInMethod) -> String {
        switch method {
        case .ownApp, .pinnedApp:
            rowEnabled(.ownApp)
                ? "Full Entra, Azure and Groups support; needs admin consent in each tenant"
                : "Unavailable — configure a client ID in Settings"
        case .azureCLI:
            "Microsoft's Azure CLI app; no consent needed; Azure resource roles only"
        case .azurePowerShell:
            "Azure resource roles only; for tenants that block the Azure CLI app"
        case .custom:
            "A registration that lists only http://localhost, such as an existing company PIM app; signs in through the browser"
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
