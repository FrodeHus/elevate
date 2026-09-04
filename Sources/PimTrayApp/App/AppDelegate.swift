import AppKit
import MSAL

// On macOS, MSAL's interactive flows use ASWebAuthenticationSession (webviewType = .authenticationSession),
// which receives its callback URL directly and completes the in-flight acquireToken call itself.
// `MSALPublicClientApplication.handleMSALResponse(_:sourceApplication:)` only exists on iOS
// (guarded by `#if TARGET_OS_IPHONE` in MSALPublicClientApplication.h), so there is nothing for
// this app delegate to forward — it only needs to exist so the app can be launched via its
// registered URL scheme.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func application(_ application: NSApplication, open urls: [URL]) {}
}
