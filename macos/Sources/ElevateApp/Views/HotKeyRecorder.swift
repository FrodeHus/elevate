import SwiftUI
import AppKit
import Carbon.HIToolbox

/// Click, press a combination; Escape cancels. Requires at least one of ⌘ ⌃ ⌥.
struct HotKeyRecorder: View {
    @Binding var binding: HotKeyBinding?
    @State private var recording = false
    @State private var monitor: Any?

    var body: some View {
        Button(recording ? "Press keys…" : (binding?.display ?? "Record shortcut")) { start() }
            .onDisappear { stop() }
    }

    private func start() {
        guard !recording else { return }
        recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            defer { stop() }
            if event.keyCode == UInt16(kVK_Escape) { return nil }
            let flags = event.modifierFlags.intersection([.command, .option, .control, .shift])
            guard !flags.intersection([.command, .option, .control]).isEmpty else { return nil }
            var carbon: UInt32 = 0
            if flags.contains(.command) { carbon |= UInt32(cmdKey) }
            if flags.contains(.option) { carbon |= UInt32(optionKey) }
            if flags.contains(.control) { carbon |= UInt32(controlKey) }
            if flags.contains(.shift) { carbon |= UInt32(shiftKey) }
            let symbols = (flags.contains(.control) ? "⌃" : "") + (flags.contains(.option) ? "⌥" : "") + (flags.contains(.shift) ? "⇧" : "") + (flags.contains(.command) ? "⌘" : "")
            let keyName = (event.charactersIgnoringModifiers ?? "?").uppercased()
            binding = HotKeyBinding(keyCode: UInt32(event.keyCode), modifiers: carbon, display: symbols + keyName)
            return nil
        }
    }

    private func stop() {
        recording = false
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
    }
}
