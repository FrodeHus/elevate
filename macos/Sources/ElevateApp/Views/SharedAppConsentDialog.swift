import SwiftUI

/// The one place the shared-app confirmation text lives, so the setup panel and Settings offer
/// the project-provided registration on identical terms.
enum SharedAppConsent {
    static let title = "Use the shared Elevate app?"
    static let confirmLabel = "Use shared app"
    static let quickStartLabel = "Quick start with the shared Elevate app…"
    static let message = """
        The Elevate project provides an optional multi-tenant app registration for quick starts \
        and testing, so you do not have to create your own. It has no client secret and only the \
        delegated permissions listed in the app registration guide. It is offered as a convenience \
        with no SLA: it may change or be withdrawn at any time. Organizations that need full \
        control should register their own app. An administrator must grant consent once per tenant \
        before sign-in works.
        """
}

/// Attaches the shared-app confirmation dialog to a view.
struct SharedAppConsentDialog: ViewModifier {
    @Binding var isPresented: Bool
    let onConfirm: () -> Void

    func body(content: Content) -> some View {
        content.confirmationDialog(SharedAppConsent.title, isPresented: $isPresented) {
            Button(SharedAppConsent.confirmLabel) { onConfirm() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(SharedAppConsent.message)
        }
    }
}

extension View {
    func sharedAppConsentDialog(isPresented: Binding<Bool>, onConfirm: @escaping () -> Void) -> some View {
        modifier(SharedAppConsentDialog(isPresented: isPresented, onConfirm: onConfirm))
    }
}
