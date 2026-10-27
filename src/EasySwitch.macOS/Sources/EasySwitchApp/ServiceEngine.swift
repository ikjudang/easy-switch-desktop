import Foundation
import EasySwitchCore
import EasySwitchEngine

/// The helper remains the sole HID owner when the UI disconnects or the user logs out.
final class ServiceEngine: EngineRuntime, @unchecked Sendable {
    private let connection = NSXPCConnection(machServiceName: "io.github.ikjudang.EasySwitch.engine", options: .privileged)
    private let lock = NSLock()
    private var polling: Task<Void, Never>?
    private let log: FileLog
    private var lastLog: String?
    init(log: FileLog) {
        self.log = log
        connection.remoteObjectInterface = NSXPCInterface(with: EngineServiceXPC.self)
        connection.resume()
    }
    deinit { connection.invalidate() }
    private func request(_ value: ServiceRequest) async throws -> Data {
        let encoded = try JSONEncoder().encode(value)
        return try await withCheckedThrowingContinuation { continuation in
            // A one-shot gate retires error, reply, and timeout races exactly once.
            let reply = ServiceReply(continuation)
            guard let remote = connection.remoteObjectProxyWithErrorHandler({ reply.finish(.failure($0)) }) as? EngineServiceXPC else {
                reply.finish(.failure(EngineError.disconnected)); return
            }
            remote.request(encoded) { data, error in
                if let data { reply.finish(.success(data)) }
                else { reply.finish(.failure(NSError(domain: "EasySwitchService", code: 1, userInfo: [NSLocalizedDescriptionKey: error ?? "No response"]))) }
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + 5) { reply.finish(.failure(EngineError.timeout)) }
        }
    }
    func start(onEvent: @escaping @Sendable (EngineEvent) -> Void) async throws {
        let initial = try JSONDecoder().decode(ServicePoll.self, from: await request(.init(operation: "poll", sequence: 0)))
        initial.events.forEach(onEvent)
        var sequence = initial.sequence
        let task = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                do {
                    let poll = try JSONDecoder().decode(ServicePoll.self, from: await request(.init(operation: "poll", sequence: sequence)))
                    sequence = poll.sequence; poll.events.forEach(onEvent)
                    if let line = poll.logs.last, line != lastLog {
                        // Include complete service records in the UI's persistent log without secrets.
                        let new = lastLog.flatMap { previous in poll.logs.lastIndex(of: previous).map { Array(poll.logs.dropFirst($0 + 1)) } } ?? poll.logs
                        new.forEach { log.write("SERVICE " + $0) }; lastLog = line
                    }
                } catch { log.write("로그인 전 엔진 연결 실패: \(error)") }
                do { try await Task.sleep(for: .milliseconds(300)) } catch { return }
            }
        }
        lock.withLock { polling?.cancel(); polling = task }
    }
    func stop() async { lock.withLock { polling?.cancel(); polling = nil }; connection.invalidate() }
    private func command(_ request: ServiceRequest) async {
        do { _ = try await self.request(request) } catch { log.write("서비스 요청 실패: \(error)") }
    }
    func configure(keyboardIDs: Set<String>, mouseIDs: Set<String>, followEnabled: Bool) async {
        await command(.init(operation: "configure", configuration: .init(keyboardIDs: keyboardIDs, mouseIDs: mouseIDs, followEnabled: followEnabled)))
    }
    func configureLAN(id: UUID, secret: Data?) async { await command(.init(operation: "relay", relayID: id, relaySecret: secret)) }
    func refresh(force: Bool) async { await command(.init(operation: "refresh")) }
    func switchChannel(_ channel: Int, source: String, presentNotice: (@Sendable (EngineEvent) async -> Void)?) async {
        do {
            let prepared = try JSONDecoder().decode(ServicePreparedSwitch.self, from: await request(.init(operation: "switch", channel: channel, source: source)))
            if let token = prepared.token, let notice = prepared.notice {
                guard let presentNotice else { log.write("서비스 전환 보류: 초기 안내 표시 경로 없음"); return }
                await presentNotice(notice)
                _ = try await request(.init(operation: "presented", presentation: token))
            }
        } catch { log.write("서비스 전환 준비 실패: \(error)") }
    }
    func switchMice(_ channel: Int, source: String = "L") async { await command(.init(operation: "mice", channel: channel)) }
    func beginPairing(_ receiverID: String) async { await command(.init(operation: "pair", receiver: receiverID)) }
    func confirmPairing(_ receiverID: String) async { await command(.init(operation: "confirmPair", receiver: receiverID)) }
    func cancelPairing(_ receiverID: String) async { await command(.init(operation: "cancelPair", receiver: receiverID)) }
}

private final class ServiceReply: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Data, Error>?
    init(_ continuation: CheckedContinuation<Data, Error>) { self.continuation = continuation }
    func finish(_ result: Result<Data, Error>) {
        let current = lock.withLock { let current = continuation; continuation = nil; return current }
        current?.resume(with: result)
    }
}
