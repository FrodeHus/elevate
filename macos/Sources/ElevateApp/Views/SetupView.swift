import SwiftUI
import ElevateCore

struct SetupView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var confirmSharedApp = false
    @State private var error: String?

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "shield.lefthalf.filled").font(.system(size: 34)).foregroundStyle(.secondary)
            Text("Complete initial setup").font(.headline)
            Text("Elevate can sign in with your own Entra app registration, which covers every role type and stays under your control. The Elevate project also offers a shared multi-tenant app for quick starts, with no SLA and an administrator consent step. Microsoft's Azure CLI app needs no registration but covers Azure resource roles only (no Entra roles or groups).")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            if model.ownAppViaLoopback {
                Text("This build is unsigned, so your own registration signs in through the browser: register http://localhost under Mobile and desktop applications rather than the msauth.… redirect.")
                    .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            }
            // Point first-time users at the guides on GitHub before they pick a route.
            DocsLinksRow()
            // Stacked buttons share one width; the primary path is prominent, the others plain.
            VStack(spacing: 8) {
                SettingsLink { Text("Open Settings…").frame(maxWidth: .infinity) }.buttonStyle(.borderedProminent)
                Button {
                    confirmSharedApp = true
                } label: { Text(SharedAppConsent.quickStartLabel).frame(maxWidth: .infinity) }
                Button {
                    openWindow(value: PanelRoute.addAccount)
                    NSApp.activate(ignoringOtherApps: true)
                } label: { Text("Continue with the Azure CLI app").frame(maxWidth: .infinity) }
            }
            .frame(width: 260)
            .padding(.top, 4)
            if let error {
                Text(error).font(.caption).foregroundStyle(.red).multilineTextAlignment(.center)
            }
        }
        .padding(20)
        .frame(maxWidth: .infinity)
        .sharedAppConsentDialog(isPresented: $confirmSharedApp) { applySharedApp() }
    }

    private func applySharedApp() {
        do {
            try model.applyClientId(AppSettings.sharedClientId)
            error = nil
        } catch {
            self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }
}
