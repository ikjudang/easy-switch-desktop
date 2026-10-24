import AppKit
import CoreServices
import EasySwitchEngine
import ServiceManagement

@MainActor
enum LoginStartup {
    static func startsInBackground(arguments: [String], event: NSAppleEventDescriptor?) -> Bool {
        if arguments.contains("--background") { return true }
        return event?.eventID == kAEOpenApplication
            && event?.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
    }

    static func registerIfNeeded(log: FileLog) {
        // Rendering and probes return before this path; an unbundled development binary
        // must not register itself as the user's login application.
        guard Bundle.main.bundleURL.pathExtension == "app",
              Bundle.main.bundleIdentifier == "io.github.ikjudang.EasySwitch" else { return }
        let service = SMAppService.mainApp
        let defaults = UserDefaults.standard
        let completedKey = "loginItemRegistrationCompleted"
        if service.status == .enabled || service.status == .requiresApproval {
            defaults.set(true, forKey: completedKey)
        }
        // Preserve a later opt-out in macOS Login Items instead of enabling it again.
        guard !defaults.bool(forKey: completedKey) else {
            log.write("로그인 자동 실행: \(description(service.status))")
            return
        }
        do {
            try service.register()
            if service.status == .enabled || service.status == .requiresApproval {
                defaults.set(true, forKey: completedKey)
            }
            log.write("로그인 자동 실행 등록: \(description(service.status))")
        } catch {
            log.write("로그인 자동 실행 등록 실패: \(error)")
        }
    }

    private static func description(_ status: SMAppService.Status) -> String {
        switch status {
        case .enabled: "enabled"
        case .requiresApproval: "requiresApproval (macOS 로그인 항목에서 허용 필요)"
        case .notRegistered: "notRegistered"
        case .notFound: "notFound"
        @unknown default: "unknown"
        }
    }
}
