import Foundation
import ServiceManagement

@MainActor
enum SystemEngineRegistration {
    static let plist = "io.github.ikjudang.EasySwitch.engine.plist"
    static var available: Bool { SMAppService.daemon(plistName: plist).status == .enabled }
    static func configure(enabled: Bool) throws -> Bool {
        let service = SMAppService.daemon(plistName: plist)
        if enabled {
            if service.status == .notRegistered || service.status == .notFound { try service.register() }
            if service.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
            return service.status == .enabled
        }
        if service.status != .notRegistered && service.status != .notFound { try service.unregister() }
        return false
    }
}
