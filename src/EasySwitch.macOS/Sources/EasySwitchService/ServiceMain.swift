import Foundation
import Security
import Darwin
import EasySwitchEngine

private actor ServiceState {
    private let log: FileLog
    private let engine: DeviceEngine
    private let configURL = URL(fileURLWithPath: "/var/db/easyswitch/engine.json")
    private var configuration: ServiceConfiguration
    private var sequence = 0
    private var presentations: [UUID: ServicePresentationGate] = [:]
    private var history: [(Int, EngineEvent)] = []
    init() throws {
        let folder = URL(fileURLWithPath: "/var/db/easyswitch", isDirectory: true)
        try Self.secureDirectory(folder)
        let logs = folder.appendingPathComponent("Logs")
        try Self.secureDirectory(logs)
        log = try FileLog(directory: logs)
        engine = DeviceEngine(log: log)
        if FileManager.default.fileExists(atPath: configURL.path) {
            let attributes = try FileManager.default.attributesOfItem(atPath: configURL.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular,
                  (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == 0 else { throw ServiceFailure.invalid }
            configuration = try JSONDecoder().decode(ServiceConfiguration.self, from: Data(contentsOf: configURL))
        } else { configuration = .init(keyboardIDs: [], mouseIDs: [], followEnabled: false) }
    }
    private static func secureDirectory(_ url: URL) throws {
        if mkdir(url.path, 0o700) != 0 && errno != EEXIST { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let descriptor = open(url.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard descriptor >= 0 else { throw ServiceFailure.invalid }
        defer { close(descriptor) }
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_uid == 0,
              fchmod(descriptor, 0o700) == 0 else { throw ServiceFailure.invalid }
    }
    func start() async throws {
        await engine.configure(keyboardIDs: configuration.keyboardIDs, mouseIDs: configuration.mouseIDs, followEnabled: configuration.followEnabled)
        try await engine.start { [weak self] event in Task { await self?.record(event) } }
        if let id = configuration.relayID { await engine.configureLAN(id: id, secret: configuration.relaySecret) }
        log.write("로그인 전 시스템 엔진 시작; 실제 HID 접근 권한은 개별 open 결과로 확인")
    }
    private func record(_ event: EngineEvent) {
        sequence += 1; history.append((sequence, event))
        if history.count > 1000 { history.removeFirst(history.count - 1000) }
    }
    func handle(_ request: ServiceRequest, uid: uid_t) async throws -> Data {
        guard configuration.owner == nil || configuration.owner == uid else { throw ServiceFailure.otherOwner }
        switch request.operation {
        case "configure":
            guard var next = request.configuration, next.keyboardIDs.count + next.mouseIDs.count <= 64,
                  next.keyboardIDs.union(next.mouseIDs).allSatisfy({ $0.count <= 1024 }) else { throw ServiceFailure.invalid }
            next.owner = uid; next.relayID = configuration.relayID; next.relaySecret = configuration.relaySecret
            try JSONEncoder().encode(next).write(to: configURL, options: [.atomic, .completeFileProtectionUnlessOpen])
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: configURL.path)
            configuration = next
            await engine.configure(keyboardIDs: next.keyboardIDs, mouseIDs: next.mouseIDs, followEnabled: next.followEnabled)
        case "poll":
            let cursor = request.sequence ?? 0
            let resync = cursor == 0 || cursor > sequence || cursor < (history.first?.0 ?? 0) - 1
            var events: [EngineEvent]
            if resync {
                events = [.devices(await engine.currentDevices())]
                if let receivers = history.last(where: { if case .receivers = $0.1 { return true }; return false }) { events.append(receivers.1) }
            } else { events = history.filter { $0.0 > cursor }.map(\.1) }
            return try JSONEncoder().encode(ServicePoll(sequence: sequence, events: events, logs: await log.recentLines()))
        case "relay":
            guard let id = request.relayID, request.relaySecret == nil || request.relaySecret?.count == 32 else { throw ServiceFailure.invalid }
            configuration.relayID = id; configuration.relaySecret = request.relaySecret; configuration.owner = uid
            try JSONEncoder().encode(configuration).write(to: configURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: configURL.path)
            await engine.configureLAN(id: id, secret: request.relaySecret)
        case "refresh": await engine.refresh(force: true)
        case "presented":
            guard let token = request.presentation, let gate = presentations[token] else { throw ServiceFailure.invalid }
            await gate.acknowledge()
        case "switch":
            guard let channel = request.channel, (1...3).contains(channel), ["B", "P", "T", "H"].contains(request.source ?? "B") else { throw ServiceFailure.invalid }
            let gate = ServicePresentationGate(), token = await gate.token
            presentations[token] = gate
            let engine = engine, source = request.source ?? "B"
            Task { [weak self] in
                await engine.switchChannel(channel, source: source, presentNotice: { event in
                    if !(await gate.present(event)), case .notice(let run, _, _, _) = event { await engine.cancelUnpresentedRequest(run) }
                })
                await gate.finish(); await self?.removePresentation(token)
            }
            Task { try? await Task.sleep(for: .seconds(4)); await gate.expire() }
            return try JSONEncoder().encode(await gate.prepared())
        case "mice":
            guard let channel = request.channel, (1...3).contains(channel) else { throw ServiceFailure.invalid }
            let engine = engine; Task { await engine.switchMice(channel) }
        case "pair", "confirmPair", "cancelPair":
            guard let receiver = request.receiver, receiver.count <= 1024 else { throw ServiceFailure.invalid }
            let engine = engine
            switch request.operation {
            case "pair": Task { await engine.beginPairing(receiver) }
            case "confirmPair": Task { await engine.confirmPairing(receiver) }
            default: Task { await engine.cancelPairing(receiver) }
            }
        default: throw ServiceFailure.invalid
        }
        return Data("{}".utf8)
    }
    private func removePresentation(_ token: UUID) { presentations[token] = nil }
    enum ServiceFailure: Error { case invalid, otherOwner }
}

private final class ServiceSession: NSObject, EngineServiceXPC, @unchecked Sendable {
    private let state: ServiceState
    private let uid: uid_t
    init(state: ServiceState, uid: uid_t) { self.state = state; self.uid = uid }
    func request(_ data: Data, reply: @escaping (Data?, String?) -> Void) {
        guard data.count <= 32768 else { reply(nil, "Request too large"); return }
        let callback = ReplyCallback(reply)
        Task {
            do { callback.send(try await state.handle(JSONDecoder().decode(ServiceRequest.self, from: data), uid: uid), nil) }
            catch { callback.send(nil, String(describing: error)) }
        }
    }
}
private final class ReplyCallback: @unchecked Sendable {
    private let callback: (Data?, String?) -> Void
    init(_ callback: @escaping (Data?, String?) -> Void) { self.callback = callback }
    func send(_ data: Data?, _ error: String?) { callback(data, error) }
}

private final class ServiceListener: NSObject, NSXPCListenerDelegate {
    private let state: ServiceState
    private let requirement: SecRequirement
    init(state: ServiceState) throws {
        self.state = state
        var own: SecCode?, ownStatic: SecStaticCode?, information: CFDictionary?
        guard SecCodeCopySelf([], &own) == errSecSuccess, let own,
              SecCodeCopyStaticCode(own, [], &ownStatic) == errSecSuccess, let ownStatic,
              SecCodeCopySigningInformation(ownStatic, SecCSFlags(rawValue: kSecCSSigningInformation), &information) == errSecSuccess,
              let team = (information as? [String: Any])?[kSecCodeInfoTeamIdentifier as String] as? String,
              team.allSatisfy({ $0.isLetter || $0.isNumber }) else { throw ServiceState.ServiceFailure.invalid }
        var requirement: SecRequirement?
        let rule = "anchor apple generic and identifier \"io.github.ikjudang.EasySwitch\" and certificate leaf[subject.OU] = \"\(team)\""
        guard SecRequirementCreateWithString(rule as CFString, [], &requirement) == errSecSuccess, let requirement else {
            throw ServiceState.ServiceFailure.invalid
        }
        self.requirement = requirement
    }
    func listener(_ listener: NSXPCListener, shouldAcceptNewConnection connection: NSXPCConnection) -> Bool {
        var code: SecCode?
        guard connection.effectiveUserIdentifier >= 500,
              SecCodeCopyGuestWithAttributes(nil, [kSecGuestAttributePid: connection.processIdentifier] as CFDictionary, [], &code) == errSecSuccess,
              let code, SecCodeCheckValidity(code, [], requirement) == errSecSuccess else { return false }
        connection.exportedInterface = NSXPCInterface(with: EngineServiceXPC.self)
        connection.exportedObject = ServiceSession(state: state, uid: connection.effectiveUserIdentifier)
        connection.resume(); return true
    }
}

@main
struct EasySwitchServiceMain {
    static func main() {
        guard geteuid() == 0 else { FileHandle.standardError.write(Data("EasySwitchService requires launchd system service registration.\n".utf8)); exit(1) }
        do {
            let state = try ServiceState()
            let delegate = try ServiceListener(state: state)
            let listener = NSXPCListener(machServiceName: "io.github.ikjudang.EasySwitch.engine")
            listener.delegate = delegate; listener.resume()
            Task { do { try await state.start() } catch { NSLog("EasySwitch service startup failed: %@", String(describing: error)); exit(1) } }
            withExtendedLifetime((listener, delegate)) { dispatchMain() }
        } catch { NSLog("EasySwitch service failed: %@", String(describing: error)); exit(1) }
    }
}
