import SwiftUI
import ElevateCore

@main
struct ElevateApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @State private var model: AppModel

    init() {
        // Bootstrap is owned by the app, not the panel view: closing the panel must not cancel it.
        let model = AppModel.live()
        _model = State(initialValue: model)
        Task { @MainActor in await model.bootstrap() }
    }

    var body: some Scene {
        MenuBarExtra {
            PanelView()
                .environment(model)
                .onAppear { model.panelOpened() }
        } label: {
            MenuBarLabel()
                .environment(model)
        }
        .menuBarExtraStyle(.window)

        WindowGroup("Elevate", for: PanelRoute.self) { $route in
            if let route {
                RouteWindow(route: route).environment(model)
            }
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.suppressed)

        Settings {
            SettingsView().environment(model)
        }
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
