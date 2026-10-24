import Foundation
import EasySwitchCore
import EasySwitchTransport

public protocol HIDDriving: Sendable {
    func start(onEvent: @escaping @Sendable (HIDEvent) -> Void) async throws
    func stop() async
    func send(deviceID: String, bytes: [UInt8]) async throws
    func snapshot() async -> [HIDDeviceDescriptor]
}

extension HIDMonitor: HIDDriving {}

public enum EngineError: Error, Sendable {
    case timeout, disconnected, unsupported, invalidResponse, protocolError([UInt8])
}

actor AsyncKeyGate {
    private var held: Set<String> = []
    private var waiters: [String: [CheckedContinuation<Void, Never>]] = [:]
    func acquire(_ key: String) async {
        if held.insert(key).inserted { return }
        await withCheckedContinuation { waiters[key, default: []].append($0) }
    }
    func release(_ key: String) {
        if var waiting = waiters[key], !waiting.isEmpty {
            let next = waiting.removeFirst()
            waiters[key] = waiting.isEmpty ? nil : waiting
            next.resume()
        } else { held.remove(key) }
    }
}

/// A slot's request is fully retired before another request to that slot starts.
/// Different receiver slots can wait for responses concurrently.
public actor RequestRouter {
    private actor Reply {
        var result: Result<[UInt8], Error>?
        var waiter: CheckedContinuation<[UInt8], Error>?
        func finish(_ value: Result<[UInt8], Error>) {
            guard result == nil else { return }
            result = value
            waiter?.resume(with: value)
            waiter = nil
        }
        func value() async throws -> [UInt8] {
            if let result { return try result.get() }
            return try await withCheckedThrowingContinuation { waiter = $0 }
        }
    }
    private struct Pending {
        let id: UUID
        let bytes: [UInt8]
        let reply: Reply
        let timeout: Task<Void, Never>
    }
    private let driver: any HIDDriving
    private let log: FileLog
    private let gate = AsyncKeyGate()
    private var pending: [String: Pending] = [:]
    public init(driver: any HIDDriving, log: FileLog) { self.driver = driver; self.log = log }
    private func key(_ deviceID: String, _ index: UInt8) -> String { "\(deviceID)#\(index)" }

    public func request(deviceID: String, responseDeviceID: String? = nil, frame: HidppFrame, timeoutMS: Int = 900) async throws -> HidppFrame {
        let route = key(responseDeviceID ?? deviceID, frame.deviceIndex)
        await gate.acquire(route)
        do {
            try Task.checkCancellation()
            let bytes = try await requestLocked(deviceID: deviceID, route: route, bytes: frame.bytes, timeoutMS: timeoutMS)
            guard !Task.isCancelled else { throw CancellationError() }
            guard HidppParser.parseError(bytes) == nil else { throw EngineError.protocolError(bytes) }
            guard let parsed = HidppParser.parse(bytes) else { throw EngineError.invalidResponse }
            await gate.release(route)
            return parsed
        } catch {
            // requestLocked never releases the slot, including cancellation and send errors.
            await gate.release(route)
            throw error
        }
    }

    private func requestLocked(deviceID: String, route: String, bytes: [UInt8], timeoutMS: Int) async throws -> [UInt8] {
        let id = UUID()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            let reply = Reply()
            let timeout = Task { [weak self] in
                do { try await Task.sleep(for: .milliseconds(timeoutMS)) }
                catch { return }
                await self?.complete(route: route, id: id, result: .failure(EngineError.timeout))
            }
            pending[route] = Pending(id: id, bytes: bytes, reply: reply, timeout: timeout)
            do {
                try Task.checkCancellation()
                log.write("TX \(deviceID) \(Self.hex(bytes))")
                // Do not retire the slot while the synchronous OS write is still running.
                try await driver.send(deviceID: deviceID, bytes: bytes)
                return try await reply.value()
            } catch {
                complete(route: route, id: id, result: .failure(error))
                throw error
            }
        } onCancel: {
            Task { await self.complete(route: route, id: id, result: .failure(CancellationError())) }
        }
    }

    public func receive(deviceID: String, bytes: [UInt8]) {
        guard bytes.count >= 7 else { return }
        let route = key(deviceID, bytes[1])
        guard let request = pending[route],
              let kind = HidppMatch.classify(request: request.bytes, response: bytes) else { return }
        if kind != .response { log.write("HID++ error layout=\(kind) \(Self.hex(bytes))") }
        complete(route: route, id: request.id, result: .success(bytes))
    }

    public func send(deviceID: String, responseDeviceID: String? = nil, frame: HidppFrame,
                     preflight: (@Sendable () async throws -> Void)? = nil) async throws {
        let route = key(responseDeviceID ?? deviceID, frame.deviceIndex)
        await gate.acquire(route)
        do {
            try Task.checkCancellation()
            try await preflight?()
            try Task.checkCancellation()
            log.write("TX \(deviceID) \(Self.hex(frame.bytes))")
            try await driver.send(deviceID: deviceID, bytes: frame.bytes)
            await gate.release(route)
        } catch { await gate.release(route); throw error }
    }

    public func disconnect(_ deviceID: String) {
        let matches = pending.filter { $0.key.hasPrefix(deviceID + "#") }
        for (route, request) in matches {
            complete(route: route, id: request.id, result: .failure(EngineError.disconnected))
        }
    }

    private func complete(route: String, id: UUID, result: Result<[UInt8], Error>) {
        guard let request = pending[route], request.id == id else { return }
        pending[route] = nil
        request.timeout.cancel()
        Task { await request.reply.finish(result) }
    }

    public static func hex(_ bytes: [UInt8]) -> String { bytes.map { String(format: "%02X", $0) }.joined(separator: " ") }
}
