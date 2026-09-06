import SwiftUI
import AppKit
import ServiceManagement
import ElevateCore

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var draft = ""
    @State private var error: String?
    @State private var confirmReplace = false
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
                    Text("\(BuildInfo.version) (\(BuildInfo.build)) · \(BuildInfo.signingDescription)")
                        .textSelection(.enabled)
                }
                LabeledContent("Updates") {
                    HStack {
                        Button("Check for updates") { checkForUpdates() }
                            .disabled(checkingUpdates)
                        if checkingUpdates { ProgressView().controlSize(.small) }
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
                TextField("Application (client) ID", text: $draft, prompt: Text("00000000-0000-0000-0000-000000000000"))
                    .textFieldStyle(.roundedBorder)
                LabeledContent("Redirect URI") {
                    HStack {
                        Text(AppSettings.redirectUri).textSelection(.enabled).font(.caption.monospaced())
                        Button { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(AppSettings.redirectUri, forType: .string) } label: { Image(systemName: "doc.on.doc") }
                            .buttonStyle(.borderless).accessibilityLabel("Copy redirect URI")
                    }
                }
                Text("Register the redirect URI under the iOS/macOS platform with bundle ID \(AppSettings.bundleId) and add the Graph PIM permissions listed in the README.")
                    .font(.caption).foregroundStyle(.secondary)
                if let error { Text(error).font(.caption).foregroundStyle(.red) }
                if saved { Text("Saved. Add your accounts from the Elevate menu.").font(.caption).foregroundStyle(.secondary) }
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
            HStack {
                Spacer()
                Button("Save") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!isSaveable)
            }
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
        .onChange(of: draft) { saved = false }
        .onChange(of: hotKey) { applyHotKey() }
        .onChange(of: hotKeyProfileId) { applyHotKey() }
        .confirmationDialog("Change client ID?", isPresented: $confirmReplace) {
            Button("Sign out and change", role: .destructive) { apply() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Saving a different client ID signs out \(model.ownAppIdentityCount) account\(model.ownAppIdentityCount == 1 ? "" : "s") that use it; you will add them again. Azure CLI and Azure PowerShell accounts are unaffected.")
        }
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

    private func save() {
        if model.identities.isEmpty { apply() } else { confirmReplace = true }
    }

    private func apply() {
        do { try model.applyClientId(draft); error = nil; saved = true } catch { self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription }
    }
}
