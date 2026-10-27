import Foundation
import Testing
@testable import EasySwitchTransport

private final class RoutingWorker: HIDWorkerSession, @unchecked Sendable {
    let configuration: HIDWorkerConfiguration
    private let lock = NSLock()
    private var descriptors: [HIDDeviceDescriptor] = []
    private var held: [CheckedContinuation<Void, Error>] = []
    private var writes: [String] = []
    private var blocking = false
    private var failNext = false
    init(_ configuration: HIDWorkerConfiguration) { self.configuration = configuration }
    func start() { configuration.started(.success(())) }
    func requestStop() { release() }
    func stop() async { requestStop() }
    func snapshot() async -> [HIDDeviceDescriptor] { lock.withLock { descriptors } }
    func send(deviceID: String, bytes: [UInt8]) async throws {
        if lock.withLock({ let fail = failNext; failNext = false; return fail }) {
            throw HIDTransportError.writeFailed(Int32(bitPattern: 0xE00002D6))
        }
        try await withCheckedThrowingContinuation { continuation in
            let hold = lock.withLock {
                writes.append(deviceID)
                if blocking { held.append(continuation); return true }
                return false
            }
            if !hold { continuation.resume() }
        }
    }
    func block() { lock.withLock { blocking = true } }
    func failOneWrite() { lock.withLock { failNext = true } }
    func release() {
        let continuations = lock.withLock { () -> [CheckedContinuation<Void, Error>] in
            blocking = false; let result = held; held.removeAll(); return result
        }
        for continuation in continuations { continuation.resume() }
    }
    var sent: [String] { lock.withLock { writes } }
    func emit(_ event: HIDEvent) {
        lock.withLock {
            if case .added(let device) = event { descriptors.removeAll { $0.id == device.id }; descriptors.append(device) }
            if case .removed(let id) = event { descriptors.removeAll { $0.id == id } }
        }
        configuration.onEvent(event)
    }
}

private final class RoutingWorkers: @unchecked Sendable {
    private let lock = NSLock()
    private var workers: [RoutingWorker] = []
    func make(_ configuration: HIDWorkerConfiguration) -> any HIDWorkerSession {
        let worker = RoutingWorker(configuration)
        lock.withLock { workers.append(worker) }
        return worker
    }
    var discovery: RoutingWorker { lock.withLock { workers.last { $0.configuration.enumerationOnly }! } }
    func device(_ physical: String) -> RoutingWorker { lock.withLock { workers.last { $0.configuration.physicalID == physical }! } }
    var deviceCount: Int { lock.withLock { workers.filter { !$0.configuration.enumerationOnly }.count } }
}

private func routingDevice(_ id: String, physical: String, receiver: Bool = false) -> HIDDeviceDescriptor {
    .init(id: id, physicalID: physical, name: id, vendorID: 0x046D, productID: receiver ? 0xC548 : 0xB366,
          transport: receiver ? "USB" : "Bluetooth", usagePairs: [.init(page: receiver ? 0xFF00 : 0xFF43, usage: receiver ? 2 : 0x0202)],
          maxInputReportSize: 20, maxOutputReportSize: 20, locationID: 0, serialNumber: physical,
          supportedReportIDs: [0x10, 0x11], isReceiver: receiver, isHidppCandidate: true, isOpen: true, openResult: 0)
}

private func routingEventually(_ condition: @escaping @Sendable () -> Bool) async -> Bool {
    for _ in 0..<1000 {
        if condition() { return true }
        try? await Task.sleep(for: .milliseconds(1))
    }
    return false
}

@Suite("Physical HID ownership and independent device writes")
struct HIDMonitorRoutingTests {
    @Test func outputTimeoutDoesNotRetireOrRecreateTheRoutingOwner() async throws {
        let registry = RoutingWorkers()
        let monitor = HIDMonitor(factory: { registry.make($0) })
        try await monitor.start { _ in }
        let device = routingDevice("fixture", physical: "fixture-link")
        registry.discovery.emit(.added(device))
        let owner = registry.device("fixture-link")
        owner.emit(.added(device)); owner.failOneWrite()
        do {
            try await monitor.send(deviceID: device.id, bytes: [0x11] + Array(repeating: 0, count: 19))
            Issue.record("Expected output timeout")
        } catch HIDTransportError.writeFailed {} catch { Issue.record("Unexpected error: \(error)") }
        #expect(await monitor.snapshot().contains { $0.id == device.id && $0.isOpen })
        try await monitor.send(deviceID: device.id, bytes: [0x11] + Array(repeating: 0, count: 19))
        #expect(registry.deviceCount == 1 && registry.device("fixture-link") === owner)
        await monitor.stop()
    }
    @Test(arguments: [false, true]) func frameTraceOptionReachesDiscoveryAndDeviceWorkers(_ enabled: Bool) async throws {
        let workers = RoutingWorkers()
        let monitor = HIDMonitor(traceFrames: enabled, factory: { workers.make($0) })
        try await monitor.start { _ in }
        #expect(workers.discovery.configuration.traceFrames == enabled)
        workers.discovery.emit(.added(routingDevice("fixture", physical: "fixture")))
        #expect(await routingEventually { workers.deviceCount == 1 })
        #expect(workers.device("fixture").configuration.traceFrames == enabled)
        await monitor.stop()
    }
    @Test func blockedKeyboardWriteDoesNotBlockAnotherBluetoothDevice() async throws {
        let registry = RoutingWorkers()
        let monitor = HIDMonitor(factory: { registry.make($0) })
        try await monitor.start { _ in }
        let keyboard = routingDevice("keyboard", physical: "keyboard-link")
        let mouse = routingDevice("mouse", physical: "mouse-link")
        registry.discovery.emit(.added(keyboard)); registry.discovery.emit(.added(mouse))
        let keyWorker = registry.device("keyboard-link"), mouseWorker = registry.device("mouse-link")
        keyWorker.emit(.added(keyboard)); mouseWorker.emit(.added(mouse))
        keyWorker.block()
        let first = Task { try await monitor.send(deviceID: keyboard.id, bytes: [0x11] + Array(repeating: 0, count: 19)) }
        #expect(await routingEventually { keyWorker.sent == [keyboard.id] })
        try await monitor.send(deviceID: mouse.id, bytes: [0x11] + Array(repeating: 0, count: 19))
        #expect(mouseWorker.sent == [mouse.id])
        #expect(registry.deviceCount == 2)
        keyWorker.release(); try await first.value
        await monitor.stop()
    }

    @Test func collectionsOfOneReceiverUseTheSamePhysicalOwner() async throws {
        let registry = RoutingWorkers()
        let monitor = HIDMonitor(factory: { registry.make($0) })
        try await monitor.start { _ in }
        let short = routingDevice("receiver-short", physical: "receiver-link", receiver: true)
        let long = routingDevice("receiver-long", physical: "receiver-link", receiver: true)
        registry.discovery.emit(.added(short)); registry.discovery.emit(.added(long))
        let owner = registry.device("receiver-link")
        owner.emit(.added(short)); owner.emit(.added(long))
        try await monitor.send(deviceID: short.id, bytes: [0x11] + Array(repeating: 0, count: 19))
        try await monitor.send(deviceID: long.id, bytes: [0x11] + Array(repeating: 0, count: 19))
        #expect(registry.deviceCount == 1)
        #expect(owner.sent == [short.id, long.id])
        #expect(await monitor.snapshot().allSatisfy(\.isOpen))
        await monitor.stop()
    }

    @Test func lateRemovalFromStoppedDiscoveryCannotRemoveTheNewRoutingOwner() async throws {
        let registry = RoutingWorkers()
        let monitor = HIDMonitor(factory: { registry.make($0) })
        try await monitor.start { _ in }
        let previous = registry.discovery
        previous.emit(.added(routingDevice("same-id", physical: "old-link")))
        await monitor.stop()
        try await monitor.start { _ in }
        let current = routingDevice("same-id", physical: "new-link")
        registry.discovery.emit(.added(current))
        registry.device("new-link").emit(.added(current))
        previous.emit(.removed(current.id))
        try await monitor.send(deviceID: current.id, bytes: [0x11] + Array(repeating: 0, count: 19))
        #expect(registry.device("new-link").sent == [current.id])
        await monitor.stop()
    }
}
