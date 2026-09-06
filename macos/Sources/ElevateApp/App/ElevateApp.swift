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
        // No TimelineView here: a MenuBarExtra label is rendered as a snapshot, and a
        // TimelineView-driven label came out blank. The model ticks `clock` every 30 s instead.
        let status = PanelStatus.compute(Array(model.active.values), now: model.clock)
        HStack(spacing: 3) {
            // Template rendition of the app icon's double chevron (Assets/MenuBarIcon).
            Image("MenuBarIcon")
            if status.activeCount > 0 { Text("\(status.activeCount)").monospacedDigit() }
            if let badge = Self.badge(for: status) { Image(systemName: badge).font(.caption2) }
            if status.pendingApproval { Image(systemName: "clock").font(.caption2) }
            if model.pendingApprovalCount > 0 { Image(systemName: "person.badge.clock").font(.caption2) }
        }
        .accessibilityLabel(Self.description(of: status, awaitingMyApproval: model.pendingApprovalCount))
        .onChange(of: model.pendingExtend) { _, key in
            guard let key else { return }
            openWindow(value: PanelRoute.activate([key]))
            NSApp.activate(ignoringOtherApps: true)
            model.pendingExtend = nil
        }
        .onChange(of: model.pendingProfileRun) { _, id in
            guard let id else { return }
            openWindow(value: PanelRoute.runProfile(id))
            NSApp.activate(ignoringOtherApps: true)
            model.pendingProfileRun = nil
        }
    }

    /// Extra glyph shown next to the chevron when an activation needs attention.
    static func badge(for s: PanelStatus) -> String? {
        s.expiringSoon ? "exclamationmark.circle.fill" : nil
    }

    static func description(of s: PanelStatus, awaitingMyApproval: Int = 0) -> String {
        var parts = [s.activeCount == 0 ? "No active roles" : "\(s.activeCount) active"]
        if s.expiringSoon { parts.append("one expiring soon") }
        if s.pendingApproval { parts.append("one awaiting approval") }
        if awaitingMyApproval > 0 { parts.append("\(awaitingMyApproval) awaiting your approval") }
        return parts.joined(separator: ", ")
    }
}
