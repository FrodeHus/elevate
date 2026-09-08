import SwiftUI

struct SetupView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "shield.lefthalf.filled").font(.system(size: 34)).foregroundStyle(.secondary)
            Text("Complete initial setup").font(.headline)
            Text("Elevate can sign in with your own Entra app registration, or with Microsoft's Azure CLI app which needs no registration but covers Azure resource roles only (no Entra roles or groups).")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            if model.ownAppViaLoopback {
                Text("This build is unsigned, so your own registration signs in through the browser: register http://localhost under Mobile and desktop applications rather than the msauth.… redirect.")
                    .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            }
            // Point first-time users at the guides on GitHub before they pick a route.
            DocsLinksRow()
            // Stacked buttons share one width; the primary path is prominent, the other plain.
            VStack(spacing: 8) {
                SettingsLink { Text("Open Settings…").frame(maxWidth: .infinity) }.buttonStyle(.borderedProminent)
                Button {
                    openWindow(value: PanelRoute.addAccount)
                    NSApp.activate(ignoringOtherApps: true)
                } label: { Text("Continue with the Azure CLI app").frame(maxWidth: .infinity) }
            }
            .frame(width: 260)
            .padding(.top, 4)
        }
        .padding(20)
        .frame(maxWidth: .infinity)
    }
}
