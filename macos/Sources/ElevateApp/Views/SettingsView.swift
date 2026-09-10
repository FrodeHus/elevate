import SwiftUI
import AppKit
import ServiceManagement
import ElevateCore

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var draft = ""
    @State private var error: String?
    @State private var confirmReplace = false
    @State private var confirmSharedApp = false
    @State private var saved = false
    @State private var hotKey: HotKeyBinding?
    @State private var hotKeyProfileId: UUID?
    /// Set once `onAppear` has mirrored settings, so the initial fill does not re-register the hot key.
    @State private var hotKeyLoaded = false
    /// Mirror of the real login-item state, so the toggle can flip back when registering fails.
    @State private var launchAtLogin = false
    @State private var launchAtLoginStatus: SMAppService.Status = .notRegistered
    @State private var checkingUpdates = false
    @State private var copiedDiagnostics = false
    @FocusState private var clientIdFocused: Bool

    var body: some View {
        Form {
            Section("General") {
                // An explicit setter, not `$launchAtLogin`: mirroring the system state in
                // `onAppear` must not look like the user flipping the switch.
                Toggle("Launch at login", isOn: Binding(get: { launchAtLogin }, set: { setLaunchAtLogin($0) }))
                if let launchError = model.launchAtLoginError {
                    Text(launchError).font(.caption).foregroundStyle(.red)
                } else if launchAtLoginStatus == .requiresApproval {
                    Text("Approve in System Settings → General → Login Items")
                        .font(.caption).foregroundStyle(.secondary)
                }
                LabeledContent("Version") {
                    // Which transport the own-app registration uses is the one thing the signing
                    // state changes for the user, so say it right where the signing state is shown.
                    Text("\(BuildInfo.version) (\(BuildInfo.build)) · \(BuildInfo.signingDescription)\(model.ownAppViaLoopback ? " · via loopback" : "")")
                        .textSelection(.enabled)
                }
                LabeledContent("Updates") {
                    if model.settings.updateCheckDisabled {
                        Text("Updates are managed by your organization").font(.caption).foregroundStyle(.secondary)
                    } else {
                        HStack {
                            Button("Check for updates") { checkForUpdates() }
                                .disabled(checkingUpdates)
                            if checkingUpdates { ProgressView().controlSize(.small) }
                        }
                    }
                }
                if let message = model.updateCheckMessage {
                    Text(message).font(.caption).foregroundStyle(.secondary)
                }
                LabeledContent("Diagnostics") {
                    HStack {
                        Button("Copy diagnostics") { copyDiagnostics() }
                        if copiedDiagnostics { Text("Copied").font(.caption).foregroundStyle(.secondary) }
                    }
                }
                Text("The report has your accounts, tenants, profiles and recent errors — never tokens or client secrets. Paste it into a bug report.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Entra app registration") {
                // Applies on Return or when focus leaves the field, like every other row here;
                // a Save button made this the one setting that did not take effect on its own.
                if model.settings.isClientIdManaged {
                    TextField("Application (client) ID", text: .constant(model.settings.clientId))
                        .disabled(true)
                    Label("Managed by your organization", systemImage: "building.2")
                        .font(.caption).foregroundStyle(.secondary)
                } else {
                    TextField("Application (client) ID", text: $draft, prompt: Text("00000000-0000-0000-0000-000000000000"))
                        .focused($clientIdFocused)
                        .onSubmit { if isSaveable { save() } }
                        .onChange(of: clientIdFocused) { _, focused in if !focused, isSaveable { save() } }
                    // The quick-start route is offered only where the id can actually be changed.
                    Button(SharedAppConsent.quickStartLabel) { confirmSharedApp = true }
                }
                if model.usesSharedApp {
                    Label("Shared Elevate app — no SLA", systemImage: "person.2")
                        .font(.caption).foregroundStyle(.secondary)
                    if let consent = model.sharedAppAdminConsentURL() {
                        Button("Grant admin consent…") { NSWorkspace.shared.open(consent) }
                    }
                }
                LabeledContent("Redirect URI") {
                    HStack {
                        Text(model.ownAppViaLoopback ? "http://localhost" : AppSettings.redirectUri)
                            .textSelection(.enabled).font(.caption.monospaced())
                        Button {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(model.ownAppViaLoopback ? "http://localhost" : AppSettings.redirectUri, forType: .string)
                        } label: { Image(systemName: "doc.on.doc") }
                            .buttonStyle(.borderless).accessibilityLabel("Copy redirect URI")
                    }
                }
                if model.usesSharedApp {
                    Text("The shared registration already lists this redirect URI.")
                        .font(.caption).foregroundStyle(.secondary)
                } else if model.ownAppViaLoopback {
                    Text("This unsigned build signs in through the browser (loopback), so register http://localhost under the Mobile and desktop applications platform instead of the msauth.… URI, and add the Graph PIM permissions listed in the app registration guide. A signed build uses the msauth.\(AppSettings.bundleId)://auth redirect under the iOS/macOS platform; registering both is harmless.")
                        .font(.caption).foregroundStyle(.secondary)
                } else {
                    Text("Register the redirect URI under the iOS/macOS platform with bundle ID \(AppSettings.bundleId) and add the Graph PIM permissions listed in the app registration guide.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                DocsLinksRow()
                if !model.settings.isClientIdManaged {
                    if let error { Text(error).font(.caption).foregroundStyle(.red) }
                    else if saved { Text("Saved. Add your accounts from the Elevate menu.").font(.caption).foregroundStyle(.secondary) }
                    else if isSaveable { Text("Press Return to apply.").font(.caption).foregroundStyle(.secondary) }
                }
            }
            Section("Global shortcut") {
                LabeledContent("Shortcut") {
                    HStack {
                        HotKeyRecorder(binding: $hotKey)
                        if hotKey != nil {
                            Button("Clear") { hotKey = nil }
                        }
                    }
                }
                Picker("Runs profile", selection: $hotKeyProfileId) {
                    Text("None").tag(UUID?.none)
                    ForEach(model.profiles) { profile in
                        Text(profile.name).tag(UUID?.some(profile.id))
                    }
                }
                Text("Runs the profile like Option-clicking its chip; opens the run sheet if input is needed.")
                    .font(.caption).foregroundStyle(.secondary)
                if let hotKeyError = model.hotKeyError {
                    Text(hotKeyError).font(.caption).foregroundStyle(.red)
                }
            }
            ManagedSection()
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .onAppear {
            draft = model.settings.clientId
            hotKey = model.settings.hotKey
            hotKeyProfileId = model.settings.hotKeyProfileId
            hotKeyLoaded = true
            launchAtLoginStatus = model.launchAtLoginStatus
            launchAtLogin = launchAtLoginStatus == .enabled
            // A menu bar app (LSUIElement) is not activated when a window opens, so Settings can land behind other apps.
            NSApp.activate(ignoringOtherApps: true)
            DispatchQueue.main.async {
                NSApp.windows.first { $0.isVisible && $0.contentView?.subviews.isEmpty == false && $0.title.localizedCaseInsensitiveContains("settings") }?.makeKeyAndOrderFront(nil)
            }
        }
        // A programmatic fill (the shared-app quick start) applies the value itself, so only
        // an edit that diverges from the stored id clears the "Saved." confirmation.
        .onChange(of: draft) {
            let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.compare(model.settings.clientId, options: .caseInsensitive) != .orderedSame { saved = false }
        }
        .onChange(of: hotKey) { applyHotKey() }
        .onChange(of: hotKeyProfileId) { applyHotKey() }
        .confirmationDialog("Change client ID?", isPresented: $confirmReplace) {
            Button("Sign out and change", role: .destructive) { apply() }
            // Put the field back, or losing focus would ask again for the same abandoned edit.
            Button("Cancel", role: .cancel) { draft = model.settings.clientId }
        } message: {
            Text("Saving a different client ID signs out \(model.ownAppIdentityCount) account\(model.ownAppIdentityCount == 1 ? "" : "s") that use it; you will add them again. Azure CLI and Azure PowerShell accounts are unaffected.")
        }
        .sharedAppConsentDialog(isPresented: $confirmSharedApp) { applySharedApp() }
    }

    /// Registers or unregisters the login item, putting the toggle back if the system refuses.
    private func setLaunchAtLogin(_ on: Bool) {
        do {
            try model.setLaunchAtLogin(on)
        } catch {
            launchAtLogin = !on
        }
        launchAtLoginStatus = model.launchAtLoginStatus
        // `.requiresApproval` means the registration is recorded but switched off by the user;
        // show the toggle as on so the caption explains what is still needed.
        launchAtLogin = launchAtLoginStatus == .enabled || launchAtLoginStatus == .requiresApproval
    }

    private func checkForUpdates() {
        checkingUpdates = true
        Task {
            await model.checkForUpdates(force: true)
            checkingUpdates = false
        }
    }

    private func copyDiagnostics() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(model.diagnosticsText(), forType: .string)
        copiedDiagnostics = true
        Task {
            try? await Task.sleep(for: .seconds(3))
            copiedDiagnostics = false
        }
    }

    private func applyHotKey() {
        guard hotKeyLoaded else { return }
        model.settings.hotKey = hotKey
        model.settings.hotKeyProfileId = hotKeyProfileId
        model.applyHotKey()
    }

    private var isSaveable: Bool {
        guard AppSettings.isValidClientId(draft) else { return false }
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed != model.settings.clientId || !model.isConfigured
    }

    /// `deferConfirmation` presents the sign-out warning one run-loop hop later. SwiftUI on macOS
    /// commonly drops a `confirmationDialog` raised from inside another dialog's dismissal, so the
    /// shared-app path (which runs from the consent dialog's action) must not present it inline.
    private func save(deferConfirmation: Bool = false) {
        guard !model.identities.isEmpty else { apply(); return }
        if deferConfirmation {
            Task { @MainActor in confirmReplace = true }
        } else {
            confirmReplace = true
        }
    }

    /// Fills the field with the shared registration and applies it, so the row reads like a save.
    /// Routed through `save()` so it gets the same `confirmReplace` sign-out warning as the
    /// text-field path; skipped entirely when the shared id is already in effect, since there is
    /// nothing to sign out.
    private func applySharedApp() {
        draft = AppSettings.sharedClientId
        guard !model.usesSharedApp else { return }
        save(deferConfirmation: true)
    }

    private func apply() {
        do { try model.applyClientId(draft); error = nil; saved = true } catch { self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription }
    }
}
