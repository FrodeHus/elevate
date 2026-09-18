import SwiftUI

// A `confirmationDialog` presented from inside the menu bar panel opens its own key window; the
// `MenuBarExtra(.window)` panel loses key status and dismisses itself before a click reaches the
// dialog's buttons, so the action never runs and `isPresented` is left true for next time. This is
// the inline replacement: a card rendered below the content it modifies, entirely within the
// panel's own window, so nothing steals focus.
extension View {
    /// Renders `content` unchanged, then — while `isPresented` — a confirmation card directly below
    /// it. `confirm` runs and `isPresented` is cleared on the destructive button; Cancel and Escape
    /// only clear `isPresented`.
    func inlineConfirmation(
        _ title: String,
        message: String,
        confirmTitle: String,
        isPresented: Binding<Bool>,
        confirm: @escaping () -> Void
    ) -> some View {
        modifier(InlineConfirmationModifier(title: title, message: message, confirmTitle: confirmTitle,
                                             isPresented: isPresented, confirm: confirm))
    }
}

private struct InlineConfirmationModifier: ViewModifier {
    let title: String
    let message: String
    let confirmTitle: String
    @Binding var isPresented: Bool
    let confirm: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            content
            if isPresented {
                InlineConfirmCard(title: title, message: message, confirmTitle: confirmTitle,
                                   cancel: { isPresented = false },
                                   confirm: { isPresented = false; confirm() })
                .transition(.opacity.combined(with: .move(edge: .top)))
            }
        }
        .animation(reduceMotion ? nil : .snappy, value: isPresented)
    }
}

private struct InlineConfirmCard: View {
    let title: String
    let message: String
    let confirmTitle: String
    let cancel: () -> Void
    let confirm: () -> Void
    @FocusState private var cancelFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.subheadline.weight(.semibold))
            Text(message).font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                // Cancel is the default (emphasized, focused) button, matching how the dialogs it
                // replaces read; Return activates it, never the destructive action beside it.
                Button("Cancel", action: cancel)
                    .keyboardShortcut(.cancelAction)
                    .buttonStyle(.borderedProminent)
                    .focused($cancelFocused)
                // `.destructive` alone does not tint a `.bordered` button on macOS; without an
                // explicit tint this read identically to Cancel, losing the "this is dangerous" cue
                // every `confirmationDialog` in the app already gives it.
                Button(confirmTitle, role: .destructive, action: confirm)
                    .buttonStyle(.bordered)
                    .tint(.red)
            }
        }
        .padding(10)
        .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
        .padding(.horizontal, PanelMetrics.headerInset)
        .padding(.bottom, 8)
        .accessibilityElement(children: .contain)
        .onAppear { cancelFocused = true }
    }
}
