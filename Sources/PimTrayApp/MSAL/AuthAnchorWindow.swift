import AppKit

/// A small always-available window MSAL can use as the presentation anchor for the browser sign-in sheet.
@MainActor
final class AuthAnchorWindow {
    private var window: NSWindow?
    private let controller = NSViewController()

    func present() -> NSViewController {
        if window == nil {
            controller.view = NSView(frame: NSRect(x: 0, y: 0, width: 420, height: 120))
            let label = NSTextField(labelWithString: "Complete sign-in in your browser…")
            label.frame = NSRect(x: 20, y: 50, width: 380, height: 20)
            label.alignment = .center
            controller.view.addSubview(label)
            let w = NSWindow(contentViewController: controller)
            w.title = "Elevate sign-in"
            w.styleMask = [.titled, .closable]
            w.isReleasedWhenClosed = false
            w.level = .floating
            w.center()
            window = w
        }
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        return controller
    }

    func dismiss() {
        window?.orderOut(nil)
    }
}
