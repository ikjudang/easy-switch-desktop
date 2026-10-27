import Foundation
import EasySwitchCore

public protocol EngineRuntime: AnyObject, Sendable {
    func start(onEvent: @escaping @Sendable (EngineEvent) -> Void) async throws
    func stop() async
    func configure(keyboardIDs: Set<String>, mouseIDs: Set<String>, followEnabled: Bool) async
    func configureLAN(id: UUID, secret: Data?) async
    func refresh(force: Bool) async
    func switchChannel(_ channel: Int, source: String, presentNotice: (@Sendable (EngineEvent) async -> Void)?) async
    func switchMice(_ channel: Int, source: String) async
    func beginPairing(_ receiverID: String) async
    func confirmPairing(_ receiverID: String) async
    func cancelPairing(_ receiverID: String) async
}

extension DeviceEngine: EngineRuntime {}

public struct ServiceConfiguration: Codable, Sendable {
    public var keyboardIDs: Set<String>
    public var mouseIDs: Set<String>
    public var followEnabled: Bool
    public var relayID: UUID?
    public var relaySecret: Data?
    public var owner: UInt32?
    public init(keyboardIDs: Set<String>, mouseIDs: Set<String>, followEnabled: Bool, owner: UInt32? = nil) {
        self.keyboardIDs = keyboardIDs; self.mouseIDs = mouseIDs; self.followEnabled = followEnabled; self.owner = owner
    }
}

public struct ServicePoll: Codable, Sendable {
    public var sequence: Int
    public var events: [EngineEvent]
    public var logs: [String]
    public init(sequence: Int, events: [EngineEvent], logs: [String]) { self.sequence = sequence; self.events = events; self.logs = logs }
}

@objc public protocol EngineServiceXPC {
    func request(_ data: Data, reply: @escaping (Data?, String?) -> Void)
}

public struct ServiceRequest: Codable, Sendable {
    public var operation: String
    public var configuration: ServiceConfiguration?
    public var channel: Int?
    public var receiver: String?
    public var relayID: UUID?
    public var relaySecret: Data?
    public var sequence: Int?
    public var presentation: UUID?
    public var source: String?
    public init(operation: String, configuration: ServiceConfiguration? = nil, channel: Int? = nil, receiver: String? = nil, sequence: Int? = nil, relayID: UUID? = nil, relaySecret: Data? = nil, presentation: UUID? = nil, source: String? = nil) {
        self.operation = operation; self.configuration = configuration; self.channel = channel; self.receiver = receiver; self.sequence = sequence; self.relayID = relayID; self.relaySecret = relaySecret; self.presentation = presentation; self.source = source
    }
}

public struct ServicePreparedSwitch: Codable, Sendable {
    public var token: UUID?
    public var notice: EngineEvent?
}

/// A headless engine waits for the connected UI to present the initial notice before writing.
/// Expiry releases the wait as rejected; the engine then invalidates only that request.
public actor ServicePresentationGate {
    public let token = UUID()
    private var notice: EngineEvent?
    private var released = false
    private var expired = false
    private var completed = false
    private var preparation: CheckedContinuation<ServicePreparedSwitch, Never>?
    private var acceptance: CheckedContinuation<Bool, Never>?
    public init() {}
    public func present(_ event: EngineEvent) async -> Bool {
        guard !expired else { return false }
        notice = event
        preparation?.resume(returning: .init(token: token, notice: event)); preparation = nil
        if released { return true }
        return await withCheckedContinuation { acceptance = $0 }
    }
    public func prepared() async -> ServicePreparedSwitch {
        if expired || completed { return .init() }
        if let notice { return .init(token: token, notice: notice) }
        return await withCheckedContinuation { preparation = $0 }
    }
    public func acknowledge() {
        guard !expired else { return }
        released = true; acceptance?.resume(returning: true); acceptance = nil
    }
    public func expire() {
        guard !released else { return }
        expired = true; acceptance?.resume(returning: false); acceptance = nil
        preparation?.resume(returning: .init()); preparation = nil
    }
    public func finish() {
        completed = true; preparation?.resume(returning: .init()); preparation = nil
    }
}
