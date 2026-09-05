import SwiftUI
import AppKit
import ElevateCore

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var draft = ""
    @State private var error: String?
    @State private var confirmReplace = false
    @State private var saved = false

    var body: some View {
        Form {
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
            // A menu bar app (LSUIElement) is not activated when a window opens, so Settings can land behind other apps.
            NSApp.activate(ignoringOtherApps: true)
            DispatchQueue.main.async {
                NSApp.windows.first { $0.isVisible && $0.contentView?.subviews.isEmpty == false && $0.title.localizedCaseInsensitiveContains("settings") }?.makeKeyAndOrderFront(nil)
            }
        }
        .onChange(of: draft) { saved = false }
        .confirmationDialog("Change client ID?", isPresented: $confirmReplace) {
            Button("Sign out and change", role: .destructive) { apply() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Saving a different client ID signs out \(model.ownAppIdentityCount) account\(model.ownAppIdentityCount == 1 ? "" : "s") that use it; you will add them again. Azure CLI and Azure PowerShell accounts are unaffected.")
        }
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
