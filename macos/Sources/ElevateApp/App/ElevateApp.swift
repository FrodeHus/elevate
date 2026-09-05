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
        TimelineView(.periodic(from: .now, by: 30)) { ctx in
            let status = PanelStatus.compute(Array(model.active.values), now: ctx.date)
            HStack(spacing: 3) {
                Image(systemName: Self.symbol(for: status))
                if status.activeCount > 0 { Text("\(status.activeCount)").monospacedDigit() }
                if status.pendingApproval { Image(systemName: "clock").font(.caption2) }
            }
            .accessibilityLabel(Self.description(of: status))
        }
        .onChange(of: model.pendingExtend) { _, key in
            guard let key else { return }
            openWindow(value: PanelRoute.activate([key]))
            NSApp.activate(ignoringOtherApps: true)
            model.pendingExtend = nil
        }
    }

    static func symbol(for s: PanelStatus) -> String {
        if s.expiringSoon { return "exclamationmark.shield.fill" }
        return s.activeCount > 0 ? "checkmark.shield.fill" : "shield"
    }

    static func description(of s: PanelStatus) -> String {
        var parts = [s.activeCount == 0 ? "No active roles" : "\(s.activeCount) active"]
        if s.expiringSoon { parts.append("one expiring soon") }
        if s.pendingApproval { parts.append("one awaiting approval") }
        return parts.joined(separator: ", ")
    }
}
