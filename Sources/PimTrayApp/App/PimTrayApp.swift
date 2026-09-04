import SwiftUI

@main
struct PimTrayApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        MenuBarExtra("PimTray", systemImage: "shield") {
            Text("PimTray").padding()
        }
        .menuBarExtraStyle(.window)
    }
}
