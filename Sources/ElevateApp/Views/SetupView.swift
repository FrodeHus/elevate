import SwiftUI

struct SetupView: View {
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "shield.lefthalf.filled").font(.system(size: 34)).foregroundStyle(.secondary)
            Text("Complete initial setup").font(.headline)
            Text("Elevate needs the application (client) ID of your Entra app registration before it can sign in.")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            SettingsLink { Text("Open Settings…") }.buttonStyle(.borderedProminent)
        }
        .padding(20)
        .frame(maxWidth: .infinity)
    }
}
