import SwiftUI
import AppKit
import PimTrayCore

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var draft = ""
    @State private var error: String?
    @State private var confirmReplace = false

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
                Text("Register the redirect URI under the iOS/macOS platform with bundle ID \(AppSettings.bundleId), enable public client flows, and add the Graph PIM permissions listed in the README.")
                    .font(.caption).foregroundStyle(.secondary)
                if let error { Text(error).font(.caption).foregroundStyle(.red) }
            }
            HStack {
                Spacer()
                Button("Save") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines) == model.settings.clientId || !AppSettings.isValidClientId(draft))
            }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .onAppear { draft = model.settings.clientId }
        .confirmationDialog("Change client ID?", isPresented: $confirmReplace) {
            Button("Sign out and change", role: .destructive) { apply() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Saving a different client ID signs out all \(model.identities.count) accounts; you will add them again.")
        }
    }

    private func save() {
        if model.identities.isEmpty { apply() } else { confirmReplace = true }
    }

    private func apply() {
        do { try model.applyClientId(draft); error = nil } catch { self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription }
    }
}
