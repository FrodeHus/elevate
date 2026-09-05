import AppKit
import Carbon.HIToolbox

struct HotKeyBinding: Codable, Equatable, Sendable {
    var keyCode: UInt32
    var modifiers: UInt32   // Carbon: cmdKey, optionKey, controlKey, shiftKey
    var display: String
}

/// One global hot key via Carbon's RegisterEventHotKey; works without Accessibility permission.
@MainActor final class HotKeyCenter {
    var onFire: (() -> Void)?
    private var ref: EventHotKeyRef?
    /// Kept only so the installed handler's lifetime is obvious; it lives as long as the app does.
    private var handler: EventHandlerRef?
    private static let signature: OSType = 0x454C_5654 // 'ELVT'

    /// The C callback cannot capture context, so it receives the unretained self pointer as
    /// `userData` and hops to the main actor before touching `HotKeyCenter` at all.
    private struct Box: @unchecked Sendable { let ptr: UnsafeMutableRawPointer }

    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, _, userData in
            guard let userData else { return noErr }
            let box = Box(ptr: userData)
            Task { @MainActor in
                let center = Unmanaged<HotKeyCenter>.fromOpaque(box.ptr).takeUnretainedValue()
                center.onFire?()
            }
            return noErr
        }, 1, &spec, selfPtr, &handler)
    }

    func register(_ b: HotKeyBinding) throws {
        unregister()
        let id = EventHotKeyID(signature: Self.signature, id: 1)
        let status = RegisterEventHotKey(b.keyCode, b.modifiers, id, GetApplicationEventTarget(), 0, &ref)
        guard status == noErr else {
            ref = nil
            throw HotKeyError.registrationFailed(status)
        }
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref); self.ref = nil }
    }

    enum HotKeyError: Error, LocalizedError {
        case registrationFailed(OSStatus)
        var errorDescription: String? { "The shortcut could not be registered; it may be in use by another app." }
    }
}
