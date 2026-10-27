import AppKit
import Carbon

/// Carbon registers only the requested chords; ordinary keyboard input is never observed.
@MainActor
final class GlobalHotkeys {
    private var handler: EventHandlerRef?
    private var registrations: [EventHotKeyRef] = []
    private let action: (Int) -> Void
    private let log: (String) -> Void
    private static let signature: OSType = 0x45535743

    init(action: @escaping (Int) -> Void, log: @escaping (String) -> Void) {
        self.action = action; self.log = log
    }

    func configure(enabled: Bool) {
        stop()
        guard enabled else { return }
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
            guard let event, let context else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            let result = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
            guard result == noErr else { return result }
            return MainActor.assumeIsolated {
                guard id.signature == GlobalHotkeys.signature, (1...3).contains(id.id) else { return OSStatus(eventNotHandledErr) }
                Unmanaged<GlobalHotkeys>.fromOpaque(context).takeUnretainedValue().action(Int(id.id))
                return noErr
            }
        }, 1, &type, Unmanaged.passUnretained(self).toOpaque(), &handler)
        guard status == noErr else { log("전역 단축키 등록 실패: \(status)"); return }
        for (index, key) in [kVK_ANSI_1, kVK_ANSI_2, kVK_ANSI_3].enumerated() {
            var reference: EventHotKeyRef?
            let result = RegisterEventHotKey(UInt32(key), UInt32(controlKey | optionKey | cmdKey),
                EventHotKeyID(signature: Self.signature, id: UInt32(index + 1)), GetApplicationEventTarget(), 0, &reference)
            guard result == noErr, let reference else {
                log("전역 단축키 ⌃⌥⌘\(index + 1) 등록 실패: \(result); 다른 앱의 단축키 확인 필요")
                stop(); return
            }
            registrations.append(reference)
        }
        log("전역 단축키 등록: ⌃⌥⌘1 / ⌃⌥⌘2 / ⌃⌥⌘3")
    }

    func stop() {
        registrations.forEach { UnregisterEventHotKey($0) }; registrations.removeAll()
        if let handler { RemoveEventHandler(handler) }; handler = nil
    }
}
