import SwiftUI
import PimTrayCore

@main
struct PimTrayApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @State private var model = AppModel.live()

    var body: some Scene {
        MenuBarExtra {
            PanelView()
                .environment(model)
                .task { await model.bootstrap() }
        } label: {
            MenuBarLabel()
                .environment(model)
        }
        .menuBarExtraStyle(.window)

        WindowGroup("PimTray", for: PanelRoute.self) { $route in
            if let route {
                RouteWindow(route: route).environment(model)
            }
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.suppressed)
    }
}

struct MenuBarLabel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: model.activeCount > 0 ? "checkmark.shield.fill" : "shield")
            if model.activeCount > 0 { Text("\(model.activeCount)").monospacedDigit() }
        }
        .onChange(of: model.pendingExtend) { _, key in
            guard let key else { return }
            openWindow(value: PanelRoute.activate([key]))
            NSApp.activate(ignoringOtherApps: true)
            model.pendingExtend = nil
        }
    }
}
