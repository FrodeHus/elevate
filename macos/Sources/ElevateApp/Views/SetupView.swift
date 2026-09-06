import SwiftUI

struct SetupView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "shield.lefthalf.filled").font(.system(size: 34)).foregroundStyle(.secondary)
            Text("Complete initial setup").font(.headline)
            Text("Elevate can sign in with your own Entra app registration, or with Microsoft's Azure CLI app which needs no registration.")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            if model.ownAppViaLoopback {
                Text("This build is unsigned, so your own registration signs in through the browser: register http://localhost under Mobile and desktop applications rather than the msauth.… redirect.")
                    .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            }
            SettingsLink { Text("Open Settings…") }.buttonStyle(.borderedProminent)
            Button("Continue with the Azure CLI app") {
                openWindow(value: PanelRoute.addAccount)
                NSApp.activate(ignoringOtherApps: true)
            }
        }
        .padding(20)
        .frame(maxWidth: .infinity)
    }
}
