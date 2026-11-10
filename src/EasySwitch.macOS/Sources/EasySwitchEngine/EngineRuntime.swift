import Foundation
import EasySwitchCore

public protocol EngineRuntime: AnyObject, Sendable {
    func start(onEvent: @escaping @Sendable (EngineEvent) -> Void) async throws
    func stop() async
    func configure(keyboardIDs: Set<String>, mouseIDs: Set<String>, followEnabled: Bool, lateDeviceSwitchEnabled: Bool) async
    func refresh(force: Bool) async
    func switchChannel(_ channel: Int, source: String, presentNotice: (@Sendable (EngineEvent) async -> Void)?) async
}

public extension EngineRuntime {
    func configure(keyboardIDs: Set<String>, mouseIDs: Set<String>, followEnabled: Bool) async {
        await configure(keyboardIDs: keyboardIDs, mouseIDs: mouseIDs, followEnabled: followEnabled, lateDeviceSwitchEnabled: true)
    }
}

extension DeviceEngine: EngineRuntime {}

public struct ServiceConfiguration: Codable, Sendable {
    public var keyboardIDs: Set<String>
    public var mouseIDs: Set<String>
    public var followEnabled: Bool
    public var lateDeviceSwitchEnabled: Bool?
    public var owner: UInt32?
    public init(keyboardIDs: Set<String>, mouseIDs: Set<String>, followEnabled: Bool, lateDeviceSwitchEnabled: Bool? = true, owner: UInt32? = nil) {
        self.keyboardIDs = keyboardIDs; self.mouseIDs = mouseIDs; self.followEnabled = followEnabled; self.owner = owner
        self.lateDeviceSwitchEnabled = lateDeviceSwitchEnabled
    }
}

public struct ServicePoll: Codable, Sendable {
    public var sequence: Int
    public var events: [EngineEvent]
    public var logs: [String]
    public init(sequence: Int, events: [EngineEvent], logs: [String]) { self.sequence = sequence; self.events = events; self.logs = logs }
    /// The helper keeps running across an app update, so it can send events this build no longer has.
    /// Skip those events; failing the whole poll would leave the app with no devices.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        sequence = try container.decode(Int.self, forKey: .sequence)
        events = try container.decode([KnownEvent].self, forKey: .events).compactMap(\.event)
        logs = try container.decode([String].self, forKey: .logs)
    }
    private struct KnownEvent: Decodable {
        let event: EngineEvent?
        init(from decoder: Decoder) { event = try? EngineEvent(from: decoder) }
    }
}

@objc public protocol EngineServiceXPC {
    func request(_ data: Data, reply: @escaping (Data?, String?) -> Void)
}

public struct ServiceRequest: Codable, Sendable {
    public var operation: String
    public var configuration: ServiceConfiguration?
    public var channel: Int?
    public var sequence: Int?
    public var presentation: UUID?
    public var source: String?
    public init(operation: String, configuration: ServiceConfiguration? = nil, channel: Int? = nil, sequence: Int? = nil, presentation: UUID? = nil, source: String? = nil) {
        self.operation = operation; self.configuration = configuration; self.channel = channel; self.sequence = sequence; self.presentation = presentation; self.source = source
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
