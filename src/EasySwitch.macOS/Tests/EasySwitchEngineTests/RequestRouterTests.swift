import Foundation
import Testing
import EasySwitchCore
import EasySwitchTransport
@testable import EasySwitchEngine

private actor RouterDriver: HIDDriving {
    struct Write: Sendable { let deviceID: String; let bytes: [UInt8] }
    private var writes: [Write] = []
    private var blocked: [CheckedContinuation<Void, Never>] = []
    private var blockFirst: Bool
    private var active = 0
    private var maximumActive = 0

    init(blockFirst: Bool = false) { self.blockFirst = blockFirst }
    func start(onEvent: @escaping @Sendable (HIDEvent) -> Void) async throws {}
    func stop() async {}
    func snapshot() async -> [HIDDeviceDescriptor] { [] }
    func send(deviceID: String, bytes: [UInt8]) async throws {
        active += 1
        maximumActive = max(maximumActive, active)
        writes.append(Write(deviceID: deviceID, bytes: bytes))
        if blockFirst {
            blockFirst = false
            // Synchronous OS writes cannot be unwound just because the awaiting task was cancelled.
            await withCheckedContinuation { blocked.append($0) }
        }
        active -= 1
    }
    func releaseWrite() {
        let continuations = blocked
        blocked = []
        for continuation in continuations { continuation.resume() }
    }
    var count: Int { writes.count }
    var maxConcurrentWrites: Int { maximumActive }
    func write(at index: Int) -> Write? { writes.indices.contains(index) ? writes[index] : nil }
}

private actor RouteGeneration {
    private var current: UInt64 = 1
    func advance() { current += 1 }
    func validate(_ expected: UInt64) throws {
        guard current == expected else { throw EngineError.disconnected }
    }
}

private func routerLog() throws -> FileLog {
    try FileLog(directory: FileManager.default.temporaryDirectory.appendingPathComponent("easyswitch-router-tests-\(UUID())"))
}

private func routerEventually(_ condition: @escaping @Sendable () async -> Bool) async -> Bool {
    for _ in 0..<500 {
        if await condition() { return true }
        try? await Task.sleep(for: .milliseconds(1))
    }
    return false
}

@Suite("HID++ request ownership and write serialization")
struct RequestRouterTests {
    @Test func correlatesPhysicalDeviceSlotAndSoftwareAndAcceptsLongResponse() async throws {
        let driver = RouterDriver()
        let router = RequestRouter(driver: driver, log: try routerLog())
        let request = try RootFeature.getFeature(0x1814, deviceIndex: 2, softwareID: 9)
        let pending = Task { try await router.request(deviceID: "receiver-A", frame: request) }
        #expect(await routerEventually { await driver.count == 1 })

        await router.receive(deviceID: "receiver-B", bytes: [0x10, 2, 0, 9, 1, 0, 0])
        await router.receive(deviceID: "receiver-A", bytes: [0x10, 3, 0, 9, 2, 0, 0])
        await router.receive(deviceID: "receiver-A", bytes: [0x10, 2, 0, 10, 3, 0, 0])
        await router.receive(deviceID: "receiver-A", bytes: [0x10, 2, 0, 0, 4, 0, 0])
        let expected: [UInt8] = [0x11, 2, 0, 9, 12] + Array(repeating: 0, count: 15)
        await router.receive(deviceID: "receiver-A", bytes: expected)
        let result = try await pending.value
        #expect(result.bytes == expected)
    }

    @Test(arguments: [[UInt8](arrayLiteral: 0x10, 2, 0xFF, 0, 9, 6, 0),
                      [UInt8](arrayLiteral: 0x10, 2, 0xFF, 9, 0, 6, 0)])
    func matchingErrorIsNotReportedAsTimeout(_ error: [UInt8]) async throws {
        let driver = RouterDriver()
        let router = RequestRouter(driver: driver, log: try routerLog())
        let request = try RootFeature.getFeature(0x1814, deviceIndex: 2, softwareID: 9)
        let pending = Task { try await router.request(deviceID: "receiver", frame: request) }
        #expect(await routerEventually { await driver.count == 1 })
        await router.receive(deviceID: "receiver", bytes: error)
        do {
            _ = try await pending.value
            Issue.record("A protocol error must be thrown")
        } catch EngineError.protocolError(let raw) {
            #expect(raw == error)
        } catch { Issue.record("Wrong error: \(error)") }
    }

    @Test func missingReplyTimesOutAndReleasesSlotForNextRequest() async throws {
        let driver = RouterDriver()
        let router = RequestRouter(driver: driver, log: try routerLog())
        let request = try RootFeature.getFeature(0x1814, softwareID: 9)
        do {
            _ = try await router.request(deviceID: "direct", frame: request, timeoutMS: 5)
            Issue.record("A missing reply must time out")
        } catch EngineError.timeout {} catch { Issue.record("Wrong error: \(error)") }
        let next = Task { try await router.request(deviceID: "direct", frame: request) }
        #expect(await routerEventually { await driver.count == 2 })
        await router.receive(deviceID: "direct", bytes: [0x10, 0xFF, 0, 9, 7, 0, 0])
        let response = try await next.value
        #expect(response.param(0) == 7)
    }

    @Test func cancellationFinishesPendingRequest() async throws {
        let driver = RouterDriver()
        let router = RequestRouter(driver: driver, log: try routerLog())
        let request = try RootFeature.getFeature(0x1814, softwareID: 9)
        let pending = Task { try await router.request(deviceID: "direct", frame: request) }
        #expect(await routerEventually { await driver.count == 1 })
        pending.cancel()
        do {
            _ = try await pending.value
            Issue.record("A cancelled request must throw cancellation")
        } catch is CancellationError {} catch { Issue.record("Wrong error: \(error)") }
    }

    @Test func disconnectRetiresOnlyMatchingPhysicalDevice() async throws {
        let driver = RouterDriver()
        let router = RequestRouter(driver: driver, log: try routerLog())
        let request = try RootFeature.getFeature(0x1814, softwareID: 9)
        let first = Task { try await router.request(deviceID: "A", frame: request) }
        let second = Task { try await router.request(deviceID: "B", frame: request) }
        #expect(await routerEventually { await driver.count == 2 })
        await router.disconnect("A")
        await router.receive(deviceID: "B", bytes: [0x10, 0xFF, 0, 9, 4, 0, 0])
        do {
            _ = try await first.value
            Issue.record("Removed device must fail its pending request")
        } catch EngineError.disconnected {} catch { Issue.record("Wrong error: \(error)") }
        #expect(try await second.value.param(0) == 4)
    }

    @Test func cancellationDuringWriteCannotOverlapNextRequestWrite() async throws {
        let driver = RouterDriver(blockFirst: true)
        let router = RequestRouter(driver: driver, log: try routerLog())
        let request = try RootFeature.getFeature(0x1814, softwareID: 9)
        let first = Task { try await router.request(deviceID: "receiver", frame: request, timeoutMS: 500) }
        #expect(await routerEventually { await driver.count == 1 })
        first.cancel()
        let second = Task { try await router.request(deviceID: "receiver", frame: request, timeoutMS: 500) }
        try await Task.sleep(for: .milliseconds(10))
        #expect(await driver.count == 1)
        await driver.releaseWrite()
        #expect(await routerEventually { await driver.count == 2 })
        await router.receive(deviceID: "receiver", bytes: [0x10, 0xFF, 0, 9, 8, 0, 0])
        do {
            _ = try await first.value
            Issue.record("First request should remain cancelled")
        } catch is CancellationError {} catch { Issue.record("Wrong error: \(error)") }
        #expect(try await second.value.param(0) == 8)
        #expect(await driver.maxConcurrentWrites == 1)
    }

    @Test func differentReceiverSlotsCanWaitIndependently() async throws {
        let driver = RouterDriver()
        let router = RequestRouter(driver: driver, log: try routerLog())
        let requestA = try RootFeature.getFeature(0x1814, deviceIndex: 1, softwareID: 9)
        let requestB = try RootFeature.getFeature(0x1814, deviceIndex: 2, softwareID: 9)
        let first = Task { try await router.request(deviceID: "receiver", frame: requestA) }
        let second = Task { try await router.request(deviceID: "receiver", frame: requestB) }
        #expect(await routerEventually { await driver.count == 2 })
        await router.receive(deviceID: "receiver", bytes: [0x10, 2, 0, 9, 6, 0, 0])
        await router.receive(deviceID: "receiver", bytes: [0x10, 1, 0, 9, 5, 0, 0])
        #expect(try await first.value.param(0) == 5)
        #expect(try await second.value.param(0) == 6)
    }

    @Test func routeChangedWhileWaitingForGateBlocksStaleHostWrite() async throws {
        let driver = RouterDriver(blockFirst: true)
        let router = RequestRouter(driver: driver, log: try routerLog())
        let generation = RouteGeneration()
        let frame = try ChangeHostFeature.setCurrentHost(featureIndex: 9, hostIndex: 1, deviceIndex: 2, softwareID: 9)
        let first = Task { try await router.send(deviceID: "long", responseDeviceID: "receiver", frame: frame) }
        #expect(await routerEventually { await driver.count == 1 })
        let stale = Task {
            try await router.send(deviceID: "short", responseDeviceID: "receiver", frame: frame) {
                try await generation.validate(1)
            }
        }
        try await Task.sleep(for: .milliseconds(10))
        await generation.advance()
        await driver.releaseWrite()
        try await first.value
        do {
            try await stale.value
            Issue.record("A changed route must fail its queued write's preflight")
        } catch EngineError.disconnected {} catch { Issue.record("Wrong error: \(error)") }
        #expect(await driver.count == 1)
        // A preflight failure must also release the gate for a newly validated request.
        try await router.send(deviceID: "long", responseDeviceID: "receiver", frame: frame)
        #expect(await driver.count == 2)
        #expect(await driver.maxConcurrentWrites == 1)
    }
}
