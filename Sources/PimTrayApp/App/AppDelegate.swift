import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    // On macOS ASWebAuthenticationSession consumes the redirect itself; handleMSALResponse is iOS-only, so nothing to forward here.
    func application(_ application: NSApplication, open urls: [URL]) {}
}
