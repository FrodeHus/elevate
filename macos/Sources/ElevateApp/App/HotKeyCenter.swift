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
    /// Read by `deinit`, which is nonisolated, and written only by `init` on the main actor: by the
    /// time deinit runs nothing else can touch it, so the unchecked annotation is safe here.
    private nonisolated(unsafe) var handler: EventHandlerRef?
    private nonisolated static let signature: OSType = 0x454C_5654 // 'ELVT'
    private nonisolated static let hotKeyId: UInt32 = 1

    /// The C callback cannot capture context, so it receives the unretained self pointer as
    /// `userData` and hops to the main actor before touching `HotKeyCenter` at all.
    private struct Box: @unchecked Sendable { let ptr: UnsafeMutableRawPointer }

    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        var installed: EventHandlerRef?
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, userData in
            guard let userData else { return noErr }
            // Other components install hot keys on the same application target; only ours may fire.
            var id = EventHotKeyID()
            let got = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                        nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
            guard got == noErr, id.signature == HotKeyCenter.signature else { return OSStatus(eventNotHandledErr) }
            let box = Box(ptr: userData)
            Task { @MainActor in
                let center = Unmanaged<HotKeyCenter>.fromOpaque(box.ptr).takeUnretainedValue()
                center.onFire?()
            }
            return noErr
        }, 1, &spec, selfPtr, &installed)
        // Keep the reference only if the handler is really installed; otherwise there is nothing to remove.
        if status == noErr { handler = installed }
    }

    deinit {
        if let handler { RemoveEventHandler(handler) }
    }

    func register(_ b: HotKeyBinding) throws {
        unregister()
        let id = EventHotKeyID(signature: Self.signature, id: Self.hotKeyId)
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
