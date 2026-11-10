import Foundation
import Testing
import EasySwitchCore
import EasySwitchTransport
@testable import EasySwitchEngine

private func discoveryEndpoint(_ id: String, physicalID: String = "receiver", receiver: Bool = true,
                               reportIDs: [Int] = [0x10, 0x11], isCandidate: Bool = true,
                               isOpen: Bool = true, openResult: Int32? = 0, serialNumber: String? = nil) -> HIDDeviceDescriptor {
    HIDDeviceDescriptor(id: id, physicalID: physicalID, name: receiver ? "Logi Bolt Receiver" : "MX KEYS S",
                        vendorID: 0x046D, productID: receiver ? 0xC548 : 0xB366,
                        transport: receiver ? "USB" : "Bluetooth",
                        usagePairs: [HIDUsagePair(page: isCandidate ? (receiver ? 0xFF00 : 0xFF43) : 1,
                                                 usage: isCandidate ? (receiver ? 2 : 0x0202) : 6)],
                        maxInputReportSize: reportIDs.contains(0x11) ? 20 : 7,
                        maxOutputReportSize: reportIDs.contains(0x11) ? 20 : 7,
                        locationID: 123, serialNumber: serialNumber, supportedReportIDs: reportIDs,
                        isReceiver: receiver, isHidppCandidate: isCandidate, isOpen: isOpen, openResult: openResult)
}

/// Responds to real request bytes instead of assigning DeviceEngine's internal state.
private actor DiscoveryDriver: HIDDriving {
    private var endpoints: [HIDDeviceDescriptor]
    private var onEvent: (@Sendable (HIDEvent) -> Void)?
    private let responsiveSlots: Set<UInt8>
    private var requests: [(device: String, bytes: [UInt8])] = []
    private var featureIndices: [UInt8: UInt8] = [:]
    private var holdFeatureLookups = false
    private var rejectedSlots: Set<UInt8> = []
    private var hostWriteErrorCode: UInt8?
    private var heldReplies: [(device: String, bytes: [UInt8])] = []
    private var heldHostInfoDevices: Set<String> = []
    private var heldHostReplies: [(device: String, bytes: [UInt8])] = []
    private var ignoredHostInfo: Set<String> = []
    private var hostWrites: [[UInt8]] = []
    private var hostWriteDevices: [String] = []
    private var hostWriteDepartures: [String: Bool] = [:]
    private var receiverFlags: [UInt8]?
    private var notificationWrites = 0
    private var metadataReplies: Bool
    private let initialReports: [(device: String, reportID: Int, bytes: [UInt8])]
    private let pauseStart: Bool
    private var startGate: CheckedContinuation<Void, Never>?

    init(endpoints: [HIDDeviceDescriptor], responsiveSlots: Set<UInt8> = [1], metadataReplies: Bool = true,
         initialReports: [(device: String, reportID: Int, bytes: [UInt8])] = [], pauseStart: Bool = false, receiverFlags: [UInt8]? = nil) {
        self.endpoints = endpoints
        self.receiverFlags = receiverFlags
        self.responsiveSlots = responsiveSlots
        self.metadataReplies = metadataReplies; self.initialReports = initialReports; self.pauseStart = pauseStart
    }
    func start(onEvent: @escaping @Sendable (HIDEvent) -> Void) async throws {
        self.onEvent = onEvent
        for endpoint in endpoints { onEvent(.added(endpoint)) }
        for report in initialReports { onEvent(.report(deviceID: report.device, reportID: report.reportID, bytes: report.bytes)) }
        if pauseStart { await withCheckedContinuation { startGate = $0 } }
    }
    func stop() async { onEvent = nil; finishStarting() }
    func finishStarting() { startGate?.resume(); startGate = nil }
    func snapshot() async -> [HIDDeviceDescriptor] { endpoints }
    func send(deviceID: String, bytes: [UInt8]) async throws {
        requests.append((deviceID, bytes))
        if bytes.count >= 7, bytes[1] == 0xFF, bytes[3] == 0, [0x80, 0x81].contains(bytes[2]), receiverFlags != nil {
            if bytes[2] == 0x80 { receiverFlags = Array(bytes[4...6]); notificationWrites += 1 }
            let reply = [UInt8(0x10), 0xFF, bytes[2], 0] + receiverFlags!
            onEvent?(.report(deviceID: deviceID, reportID: 0x10, bytes: reply)); return
        }
        guard bytes.count >= 7, responsiveSlots.contains(bytes[1]), endpoints.contains(where: { $0.id == deviceID }) else { return }
        var parameters: [UInt8]
        let slot = bytes[1]
        if rejectedSlots.contains(slot) {
            // HID++ 1.0 error 9 (invalid device index): the receiver slot is empty for now.
            onEvent?(.report(deviceID: deviceID, reportID: 0x10, bytes: [0x10, slot, 0x8F, bytes[2], bytes[3], 9, 0])); return
        }
        let changeHostIndex = featureIndices[slot] ?? 9
        let isMouse = slot == 2 || deviceID.contains("mouse")
        let name = Array((isMouse ? "MX Master 3S" : "MX KEYS S").utf8)
        if bytes[2] == 3 && !metadataReplies { return }
        switch (bytes[2], bytes[3] >> 4) {
        case (0, 0):
            let featureID = Int(bytes[4]) << 8 | Int(bytes[5])
            parameters = [featureID == 0x1814 ? changeHostIndex : (featureID == 5 ? 3 : 0), 0, 0]
        case (let feature, 0) where feature == changeHostIndex: parameters = [3, 0]
        case (let feature, 1) where feature == changeHostIndex:
            hostWrites.append(bytes)
            hostWriteDevices.append(deviceID)
            if let code = hostWriteErrorCode {
                onEvent?(.report(deviceID: deviceID, reportID: 0x10, bytes: [0x10, slot, 0xFF, bytes[2], bytes[3], code, 0]))
            }
            if let echo = hostWriteDepartures[deviceID] {
                if echo {
                    onEvent?(.report(deviceID: deviceID, reportID: 0x11,
                        bytes: [0x11, slot, changeHostIndex, 0, 0, bytes[4]] + Array(repeating: 0, count: 14)))
                }
                remove(deviceID)
            }
            return
        case (3, 2): parameters = [isMouse ? 3 : 0]
        case (3, 0): parameters = [UInt8(name.count)]
        case (3, 1): parameters = Array(name.dropFirst(Int(bytes[4])).prefix(16))
        default: return
        }
        // A short request can produce a long reply, including a full name chunk.
        let response: [UInt8] = [0x11, slot, bytes[2], bytes[3]] + parameters + Array(repeating: 0, count: 16 - parameters.count)
        if holdFeatureLookups, bytes[2] == 0, bytes[4] == 0x18, bytes[5] == 0x14 {
            heldReplies.append((deviceID, response))
            return
        }
        if heldHostInfoDevices.contains(deviceID), bytes[2] == changeHostIndex, bytes[3] >> 4 == 0 {
            heldHostReplies.append((deviceID, response))
            return
        }
        if bytes[2] == changeHostIndex, bytes[3] >> 4 == 0, ignoredHostInfo.remove(deviceID) != nil { return }
        onEvent?(.report(deviceID: deviceID, reportID: 0x11, bytes: response))
    }
    func remove(_ id: String) {
        endpoints.removeAll { $0.id == id }
        onEvent?(.removed(id))
    }
    func add(_ endpoint: HIDDeviceDescriptor) {
        endpoints.append(endpoint)
        onEvent?(.added(endpoint))
    }
    func injectReport(deviceID: String, reportID: Int, bytes: [UInt8]) {
        onEvent?(.report(deviceID: deviceID, reportID: reportID, bytes: bytes))
    }
    func sendCount(slot: UInt8) -> Int { requests.filter { $0.bytes.count > 1 && $0.bytes[1] == slot }.count }
    var endpointIDsUsed: Set<String> { Set(requests.map(\.device)) }
    var notificationWriteCount: Int { notificationWrites }
    var notificationFlags: [UInt8]? { receiverFlags }
    var hostWriteCount: Int { hostWrites.count }
    var hostWriteEndpoints: [String] { hostWriteDevices }
    func hostWriteTargets(deviceID: String) -> [Int] {
        zip(hostWriteDevices, hostWrites).filter { $0.0 == deviceID }.map { Int($0.1[4]) }
    }
    func holdHostInfo(deviceID: String) { heldHostInfoDevices.insert(deviceID) }
    func ignoreNextHostInfo(deviceID: String) { ignoredHostInfo.insert(deviceID) }
    func hostInfoRequestCount(deviceID: String) -> Int {
        requests.filter { $0.device == deviceID && $0.bytes[2] == (featureIndices[$0.bytes[1]] ?? 9) && $0.bytes[3] >> 4 == 0 }.count
    }
    var heldHostInfoCount: Int { heldHostReplies.count }
    func releaseHostInfo() {
        heldHostInfoDevices.removeAll()
        let replies = heldHostReplies
        heldHostReplies.removeAll()
        for reply in replies { onEvent?(.report(deviceID: reply.device, reportID: 0x11, bytes: reply.bytes)) }
    }
    var metadataRequestCount: Int { requests.filter { $0.bytes.count >= 7 && $0.bytes[2] == 3 }.count }
    func suppressMetadataReplies() { metadataReplies = false }
    var featureLookupCount: Int {
        requests.filter { $0.bytes.count >= 7 && $0.bytes[2] == 0 && $0.bytes[4] == 0x18 && $0.bytes[5] == 0x14 }.count
    }
    func connectionNotice(deviceID: String, slot: UInt8, productID: Int) {
        let notice: [UInt8] = [0x10, slot, 0x41, 0x10, 1, UInt8(productID & 255), UInt8(productID >> 8)]
        onEvent?(.report(deviceID: deviceID, reportID: 0x10, bytes: notice))
    }
    func useChangeHostIndex(slot: UInt8, index: UInt8) { featureIndices[slot] = index }
    func reject(slot: UInt8) { rejectedSlots.insert(slot) }
    func answerHostWrites(withError code: UInt8) { hostWriteErrorCode = code }
    func departAfterHostWrite(deviceID: String, echo: Bool = false) { hostWriteDepartures[deviceID] = echo }
    func accept(slot: UInt8) { rejectedSlots.remove(slot) }
    func changeFeatureIndex(slot: UInt8, index: UInt8) {
        featureIndices[slot] = index
        holdFeatureLookups = true
    }
    func releaseFeatureLookups() {
        holdFeatureLookups = false
        let replies = heldReplies
        heldReplies = []
        for reply in replies { onEvent?(.report(deviceID: reply.device, reportID: 0x11, bytes: reply.bytes)) }
    }
}

private actor DiscoveryEvents {
    private var invalidated: Set<String> = []
    private var connected: [NoticeRow] = []
    private var switchRows: [NoticeRow] = []
    func record(_ event: EngineEvent) {
        if case .selectionInvalidated(let id) = event { invalidated.insert(id) }
        if case .notice(_, _, let rows, connection: true) = event { connected.append(contentsOf: rows) }
        if case .notice(_, _, let rows, connection: false) = event { switchRows.append(contentsOf: rows) }
    }
    func containsInvalidation(_ id: String) -> Bool { invalidated.contains(id) }
    var connectionNoticeCount: Int { connected.count }
    func switchStates(_ id: String) -> [NoticeState] { switchRows.filter { $0.id == id }.map(\.state) }
    func switchNotices(_ id: String) -> [NoticeRow] { switchRows.filter { $0.id == id } }
}

private actor ManualNoticeGate {
    private var waiter: CheckedContinuation<Void, Never>?
    private(set) var entered = false
    func present() async {
        entered = true
        await withCheckedContinuation { waiter = $0 }
    }
    func release() { waiter?.resume(); waiter = nil }
}

private final class PermissionEvents: @unchecked Sendable {
    private let lock = NSLock()
    private var permissionRequests = 0
    private var events = 0
    private var scans: [Bool] = []
    func record(_ event: EngineEvent) {
        lock.lock()
        defer { lock.unlock() }
        events += 1
        if case .inputMonitoringRequired = event { permissionRequests += 1 }
        if case .scanning(let value) = event { scans.append(value) }
    }
    var scanStates: [Bool] { lock.lock(); defer { lock.unlock() }; return scans }
    var requestCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return permissionRequests
    }
    var eventCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return events
    }
}

private enum NoPermissionScenario: String, CaseIterable, Sendable, CustomTestStringConvertible {
    case noHardware, nonVendorCollection, successfulOpen, unrelatedOpenFailure, noOpenResult
    var testDescription: String { rawValue }
}

private func discoveryLog(traceFrames: Bool = false) throws -> FileLog {
    try FileLog(directory: FileManager.default.temporaryDirectory.appendingPathComponent("easyswitch-discovery-tests-\(UUID())"), traceFrames: traceFrames)
}

private func discoveryEventually(attempts: Int = 1_000, _ condition: @escaping @Sendable () async -> Bool) async -> Bool {
    for _ in 0..<attempts {
        if await condition() { return true }
        try? await Task.sleep(for: .milliseconds(1))
    }
    return false
}

@Suite("Discovery through real HID++ request and callback boundaries")
struct DeviceEngineTests {
    @Test func lateManualSwitchWaitsForKeyboardAfterEasySwitchKeyDeparture() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        await driver.injectReport(deviceID: keyboard.id, reportID: 0x11,
            bytes: [0x11, 0xFF, 9, 0, 0, 2] + Array(repeating: 0, count: 14))
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("[keyboard#255] 출발 신호: 채널 3") } })
        await driver.remove(keyboard.id)
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == false })
        let keyboardBack = discoveryEndpoint("keyboard-back", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        await driver.departAfterHostWrite(deviceID: keyboardBack.id)
        let request = Task { await engine.switchChannel(3, source: "P") }
        try await Task.sleep(for: .milliseconds(500))
        await driver.add(keyboardBack)
        await request.value
        #expect(await driver.hostWriteTargets(deviceID: keyboardBack.id) == [2])
        await engine.stop()
    }

    @Test func lateManualSwitchWaitsForKeyboardAfterDepartureEcho() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let mouse = discoveryEndpoint("mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard, mouse], responsiveSlots: [0xFF])
        await driver.departAfterHostWrite(deviceID: keyboard.id, echo: true)
        await driver.departAfterHostWrite(deviceID: mouse.id)
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: "mouse#255", followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().filter(\.online).count == 2 })
        await engine.switchChannel(3, source: "P")
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [2])
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("따라가기 생략: 수동 요청 에코") } })
        let mouseBack = discoveryEndpoint("mouse-back", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        await driver.add(mouseBack)
        #expect(await discoveryEventually { await engine.currentDevices().first { $0.id == "mouse#255" }?.online == true })
        let keyboardBack = discoveryEndpoint("keyboard-back", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        await driver.departAfterHostWrite(deviceID: keyboardBack.id)
        let request = Task { await engine.switchChannel(3, source: "P") }
        try await Task.sleep(for: .milliseconds(500))
        await driver.add(keyboardBack)
        await request.value
        #expect(await driver.hostWriteTargets(deviceID: keyboardBack.id) == [2])
        await engine.stop()
    }

    @Test func manualSwitchSendsOnceToKeyboardArrivingOneSecondLater() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [], responsiveSlots: [0xFF])
        await driver.departAfterHostWrite(deviceID: keyboard.id)
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        let events = DiscoveryEvents()
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { event in Task { await events.record(event) } }
        let request = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("수동 전환 대기: 미연결 기기 연결 대기; 최대 3초") } })
        #expect(await discoveryEventually { await events.switchStates("keyboard#255") == [.switching] })
        try await Task.sleep(for: .seconds(1))
        await driver.add(keyboard)
        #expect(await discoveryEventually { await driver.hostWriteTargets(deviceID: keyboard.id) == [1] })
        await request.value
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [1])
        #expect(await discoveryEventually { await events.switchStates("keyboard#255").last == .complete })
        await engine.stop()
    }

    @Test func manualSwitchExpiresAfterThreeSecondsWithoutKeyboardAndDoesNotReplay() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [], responsiveSlots: [0xFF])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        let events = DiscoveryEvents()
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { event in Task { await events.record(event) } }
        let start = ContinuousClock.now
        await engine.switchChannel(2)
        #expect(start.duration(to: .now) >= .seconds(3))
        #expect(await driver.hostWriteCount == 0)
        #expect(await log.recentLines().contains { $0.contains("[keyboard#255] 수동 전환 대기 만료: 3초 안에 연결 안 됨") })
        #expect(await log.recentCodes().contains { $0.contains("|CAN_EXP|") })
        #expect(await discoveryEventually { await events.switchStates("keyboard#255").last == .unconfirmed })
        await driver.add(keyboard)
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        try await Task.sleep(for: .milliseconds(150))
        #expect(await driver.hostWriteCount == 0)
        await engine.stop()
    }

    @Test func disabledLateDeviceSwitchSkipsAbsentKeyboard() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [], responsiveSlots: [0xFF])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true, lateDeviceSwitchEnabled: false)
        try await engine.start { _ in }
        await engine.switchChannel(2)
        await driver.add(keyboard)
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        try await Task.sleep(for: .milliseconds(150))
        #expect(await driver.hostWriteCount == 0)
        #expect(await log.recentLines().contains { $0.contains("[keyboard#255] 수동 전환 생략: 선택한 기기의 로컬 연결 없음") })
        #expect(await log.recentCodes().allSatisfy { !$0.contains("|WAIT|") })
        await engine.stop()
    }

    @Test func newerManualRequestCancelsLateWaitAndOnlySendsNewDestination() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [], responsiveSlots: [0xFF])
        await driver.departAfterHostWrite(deviceID: keyboard.id)
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        let first = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|R0001|") && $0.contains("|WAIT|") } })
        let second = Task { await engine.switchChannel(3) }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|R0002|") && $0.contains("|WAIT|") } })
        await first.value
        #expect(await log.recentLines().contains { $0.contains("수동 전환 대기 취소:") })
        await driver.add(keyboard)
        #expect(await discoveryEventually { await driver.hostWriteTargets(deviceID: keyboard.id) == [2] })
        await second.value
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [2])
        await engine.stop()
    }

    @Test func lateKeyboardDepartureEchoDoesNotStartAnotherMouseFollow() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let mouse = discoveryEndpoint("mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [mouse], responsiveSlots: [0xFF])
        await driver.departAfterHostWrite(deviceID: mouse.id)
        await driver.departAfterHostWrite(deviceID: keyboard.id, echo: true)
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        let events = DiscoveryEvents()
        await engine.configure(keyboardID: "keyboard#255", mouseID: "mouse#255", followEnabled: true)
        try await engine.start { event in Task { await events.record(event) } }
        #expect(await discoveryEventually { await engine.currentDevices().first?.kind == "마우스" })
        let request = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { await events.switchStates("mouse#255").last == .complete })
        #expect(await driver.hostWriteTargets(deviceID: mouse.id) == [1])
        await driver.add(keyboard)
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("따라가기 생략: 수동 요청 에코") } })
        await request.value
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [1])
        #expect(await driver.hostWriteTargets(deviceID: mouse.id) == [1])
        #expect(await events.switchStates("mouse#255").allSatisfy { $0 != .unconfirmed })
        #expect(await log.recentLines().allSatisfy { !$0.contains("따라가기 대기 만료") })
        await engine.stop()
    }

    @Test func disabledLateDeviceSwitchExcludesPreparingMouseWithoutNoticeOrDeferredWrite() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let mouse = discoveryEndpoint("mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard, mouse], responsiveSlots: [0xFF])
        await driver.holdHostInfo(deviceID: mouse.id)
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        let events = DiscoveryEvents()
        await engine.configure(keyboardID: "keyboard#255", mouseID: "mouse#255", followEnabled: true, lateDeviceSwitchEnabled: false)
        try await engine.start { event in Task { await events.record(event) } }
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.id == "keyboard#255" && $0.kind == "키보드" } })
        #expect(await discoveryEventually { await driver.heldHostInfoCount > 0 })
        await driver.injectReport(deviceID: keyboard.id, reportID: 0x11,
            bytes: [0x11, 0xFF, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("[mouse#255] 따라가기 생략: 미연결 기기 연결 대기 꺼짐") } })
        await driver.releaseHostInfo()
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.id == "mouse#255" && $0.online } })
        try await Task.sleep(for: .milliseconds(150))
        #expect(await driver.hostWriteCount == 0)
        #expect(await events.switchStates("mouse#255").isEmpty)
        await engine.stop()
    }

    @Test func serviceConfigurationWithoutLateDeviceFieldDefaultsToEnabled() throws {
        let json = #"{"keyboardIDs":["keyboard#255"],"mouseIDs":[],"followEnabled":true}"#
        let configuration = try JSONDecoder().decode(ServiceConfiguration.self, from: Data(json.utf8))
        #expect(configuration.lateDeviceSwitchEnabled ?? true)
        #expect(ServiceConfiguration(keyboardIDs: [], mouseIDs: [], followEnabled: false).lateDeviceSwitchEnabled == true)
    }

    @Test(arguments: [true, false, nil] as [Bool?])
    func serviceConfigurationRoundTripsLateDeviceField(_ enabled: Bool?) throws {
        let original = ServiceConfiguration(keyboardIDs: ["keyboard#255"], mouseIDs: ["mouse#255"],
                                            followEnabled: true, lateDeviceSwitchEnabled: enabled, owner: 501)
        let decoded = try JSONDecoder().decode(ServiceConfiguration.self, from: JSONEncoder().encode(original))
        #expect(decoded.keyboardIDs == original.keyboardIDs && decoded.mouseIDs == original.mouseIDs)
        #expect(decoded.followEnabled == original.followEnabled && decoded.owner == original.owner)
        #expect(decoded.lateDeviceSwitchEnabled == enabled)
    }

    @Test(arguments: ["P", "T"])
    func lateManualSwitchUsesTheSameDestinationFromFloatingAndTrayButtons(_ source: String) async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [], responsiveSlots: [0xFF])
        await driver.departAfterHostWrite(deviceID: keyboard.id)
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        let request = Task { await engine.switchChannel(3, source: source) }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|\(source)3|") && $0.contains("|WAIT|") } })
        await driver.add(keyboard)
        await request.value
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [2])
        await engine.stop()
    }

    @Test(arguments: ["키보드", "마우스"])
    func lateManualNoticeUsesDeviceKindWhenTheNameIsUnknown(_ kind: String) async throws {
        let driver = DiscoveryDriver(endpoints: [])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        let events = DiscoveryEvents()
        await engine.configure(keyboardID: kind == "키보드" ? "unknown#255" : nil,
                               mouseID: kind == "마우스" ? "unknown#255" : nil, followEnabled: true)
        try await engine.start { event in Task { await events.record(event) } }
        let request = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { await events.switchStates("unknown#255") == [.switching] })
        let row = await events.switchNotices("unknown#255").first
        #expect(row?.name == kind && row?.kind == kind)
        request.cancel()
        await request.value
        #expect(await discoveryEventually { await events.switchStates("unknown#255").last == .unconfirmed })
        await engine.stop()
    }

    @Test(arguments: ["selection", "toggle", "stop", "task"])
    func lateManualWaitCancellationNeverReplaysWhenTheKeyboardArrives(_ reason: String) async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [], responsiveSlots: [0xFF])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        let request = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|WAIT|") } })
        switch reason {
        case "selection": await engine.configure(keyboardID: nil, mouseID: nil, followEnabled: true)
        case "toggle": await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true, lateDeviceSwitchEnabled: false)
        case "stop": await engine.stop()
        default: request.cancel()
        }
        await request.value
        #expect(await log.recentLines().contains { $0.contains("수동 전환 대기 취소:") })
        #expect(await log.recentCodes().contains { $0.contains("|CAN_PREP|") })
        await driver.add(keyboard)
        if reason != "stop" {
            #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        }
        #expect(await driver.hostWriteCount == 0)
        await engine.stop()
    }

    @Test func lateManualWaitRetainsKnownNameAcrossBluetoothReattachment() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        let events = DiscoveryEvents()
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { event in Task { await events.record(event) } }
        #expect(await discoveryEventually { await engine.currentDevices().first?.kind == "키보드" })
        await driver.remove(keyboard.id)
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == false })
        let request = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { await events.switchStates("keyboard#255") == [.switching] })
        #expect(await events.switchNotices("keyboard#255").first?.name == "MX KEYS S")
        let returned = discoveryEndpoint("keyboard-returned", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        await driver.departAfterHostWrite(deviceID: returned.id)
        await driver.add(returned)
        await request.value
        #expect(await driver.hostWriteTargets(deviceID: returned.id) == [1])
        await engine.stop()
    }

    @Test func lateManualDeadlineIncludesTimeSpentPresentingTheInitialNotice() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [], responsiveSlots: [0xFF])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        let gate = ManualNoticeGate()
        let request = Task { await engine.switchChannel(2, presentNotice: { _ in await gate.present() }) }
        #expect(await discoveryEventually { await gate.entered })
        await driver.add(keyboard)
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        #expect(await driver.hostWriteCount == 0)
        try await Task.sleep(for: .milliseconds(3_100))
        await gate.release()
        await request.value
        #expect(await driver.hostWriteCount == 0)
        #expect(await log.recentLines().contains { $0.contains("수동 전환 대기 만료: 3초 안에 연결 안 됨") })
        await engine.stop()
    }

    @Test func keyboardDepartureCancelsItsLateManualWait() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let mouse = discoveryEndpoint("mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [mouse], responsiveSlots: [0xFF])
        await driver.departAfterHostWrite(deviceID: mouse.id)
        await driver.holdHostInfo(deviceID: keyboard.id)
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: "mouse#255", followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        let request = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { await driver.hostWriteTargets(deviceID: mouse.id) == [1] })
        await driver.add(keyboard)
        #expect(await discoveryEventually { await driver.heldHostInfoCount > 0 })
        await driver.injectReport(deviceID: keyboard.id, reportID: 0x11,
            bytes: [0x11, 0xFF, 9, 0, 0, 2] + Array(repeating: 0, count: 14))
        await request.value
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id).isEmpty)
        #expect(await log.recentLines().contains { $0.contains("수동 전환 대기 취소:") })
        await engine.stop()
    }

    @Test(arguments: [false, true]) func packetModeKeepsConnectionsAndNeverRecordsOrdinaryInput(_ trace: Bool) async throws {
        let endpoint = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [endpoint], responsiveSlots: [0xFF])
        let log = try FileLog(directory: FileManager.default.temporaryDirectory.appendingPathComponent("easyswitch-log-mode-\(UUID())"), traceFrames: trace)
        let engine = DeviceEngine(driver: driver, log: log)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.kind == "키보드" } })
        await driver.injectReport(deviceID: endpoint.id, reportID: 1, bytes: Array("ORDINARY-INPUT-SENTINEL".utf8))
        await driver.remove(endpoint.id)
        #expect(await discoveryEventually { await engine.currentDevices().allSatisfy { !$0.online } })
        await engine.stop()
        let lines = await log.recentLines()
        #expect(lines.contains { $0.contains(" TX ") } == trace)
        #expect(lines.contains { $0.contains(" RX ") } == trace)
        #expect(lines.contains { $0.contains("기기 연결 확인") })
        #expect(lines.contains { $0.contains("로컬 HID 통로 종료") })
        #expect(!lines.contains { $0.contains("ORDINARY-INPUT-SENTINEL") })
    }
    @Test func idleRefreshDoesNotFloodUIAndRestartPublishesInitialStateAgain() async throws {
        let engine = DeviceEngine(driver: DiscoveryDriver(endpoints: []), log: try discoveryLog())
        let events = PermissionEvents()
        try await engine.start { events.record($0) }
        for _ in 0..<100 { await engine.refresh() }
        #expect(events.scanStates == [false])
        await engine.stop()
        try await engine.start { events.record($0) }
        for _ in 0..<100 { await engine.refresh() }
        #expect(events.scanStates == [false, false])
        await engine.stop()
    }

    @Test func initialSignalOverflowKeepsItsReasonWithoutFrameDiagnostics() async throws {
        let endpoint = discoveryEndpoint("fixture", physicalID: "fixture", receiver: false, reportIDs: [0x11])
        let frame: [UInt8] = [0x11, 0xFF, 9, 0, 0, 1] + Array(repeating: 0, count: 14)
        let driver = DiscoveryDriver(endpoints: [endpoint], responsiveSlots: [0xFF],
            initialReports: Array(repeating: (device: "fixture", reportID: 0x11, bytes: frame), count: 17))
        await driver.changeFeatureIndex(slot: 0xFF, index: 9)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        try await engine.start { _ in }
        #expect(await discoveryEventually {
            await log.recentLines().contains { $0.contains("초기 신호 보관 한도 초과: 이전 신호 한 개 폐기") }
        })
        #expect(await log.recentLines().allSatisfy { !$0.contains(" RX ") && !$0.contains(" TX ") })
        await engine.stop()
    }

    @Test func receiverLinkChangesRemainInBasicLogs() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [2])
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.kind == "마우스" && $0.online } })
        await driver.holdHostInfo(deviceID: "bolt")
        await driver.injectReport(deviceID: "bolt", reportID: 0x10, bytes: [0x10, 2, 0x41, 0x10, 0x41, 0x34, 0xB0])
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("수신기 기기 연결 해제") } })
        await driver.connectionNotice(deviceID: "bolt", slot: 2, productID: 0xB034)
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("수신기 기기 연결;") } })
        #expect(await log.recentLines().allSatisfy { !$0.contains(" RX ") && !$0.contains(" TX ") && !$0.contains("flags=") })
        await engine.stop()
    }
    @Test func manualSwitchIncludesAllSelectedDevicesAndExcludesUnselectedMouse() async throws {
        let names = ["keyboard-a", "keyboard-b", "mouse-a", "mouse-b", "mouse-unselected"]
        let endpoints = names.map { discoveryEndpoint($0, physicalID: $0, receiver: false, reportIDs: [0x11]) }
        let driver = DiscoveryDriver(endpoints: endpoints, responsiveSlots: [0xFF])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        await engine.configure(keyboardIDs: ["keyboard-a#255", "keyboard-b#255"], mouseIDs: ["mouse-a#255", "mouse-b#255"], followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().filter(\.online).count == 5 })
        let request = Task { await engine.switchChannel(2) }
        #expect(await discoveryEventually { Set(await driver.hostWriteEndpoints) == Set(names.prefix(4)) })
        #expect(await driver.hostWriteTargets(deviceID: "mouse-unselected").isEmpty)
        await engine.stop(); await request.value
    }

    @Test(arguments: [false, true], [false, true])
    func localDepartureCompletesNoticeAfterManualOrFollowSwitch(_ receiver: Bool, _ follow: Bool) async throws {
        let keyboard = discoveryEndpoint(receiver ? "bolt" : "keyboard", physicalID: receiver ? "receiver" : "keyboard",
                                         receiver: receiver, reportIDs: [0x11])
        let mouse = receiver ? keyboard : discoveryEndpoint("mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let keyboardID = receiver ? "receiver#1" : "keyboard#255"
        let mouseID = receiver ? "receiver#2" : "mouse#255"
        let driver = DiscoveryDriver(endpoints: receiver ? [keyboard] : [keyboard, mouse],
                                     responsiveSlots: receiver ? [1, 2] : [0xFF])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        let events = DiscoveryEvents()
        await engine.configure(keyboardID: keyboardID, mouseID: mouseID, followEnabled: true)
        try await engine.start { event in Task { await events.record(event) } }
        #expect(await discoveryEventually { await engine.currentDevices().filter { $0.online && $0.kind != "기기" }.count == 2 })
        // Keep background host queries from falsely reporting that a departed receiver device returned.
        await driver.holdHostInfo(deviceID: keyboard.id)
        await driver.holdHostInfo(deviceID: mouse.id)
        let request = Task {
            if follow {
                await driver.injectReport(deviceID: keyboard.id, reportID: 0x11,
                    bytes: [0x11, receiver ? 1 : 0xFF, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
            } else { await engine.switchChannel(2) }
        }
        #expect(await discoveryEventually { await driver.hostWriteCount == (follow ? 1 : 2) })
        #expect(await discoveryEventually { await events.switchStates(mouseID).contains(.switching) })
        #expect(await events.switchStates(mouseID).allSatisfy { $0 != .complete })
        if receiver {
            for slot: UInt8 in follow ? [2] : [1, 2] {
                await driver.injectReport(deviceID: mouse.id, reportID: 0x10,
                    bytes: [0x10, slot, 0x41, 0x10, 0x41, 0x34, 0xB0])
            }
        } else {
            await driver.remove(mouse.id)
            if !follow { await driver.remove(keyboard.id) }
        }
        #expect(await discoveryEventually { await events.switchStates(mouseID).last == .complete })
        #expect(await discoveryEventually { await events.switchStates(keyboardID).last == .complete })
        await request.value
        let lines = await log.recentLines()
        #expect(lines.contains { $0.contains("SWITCH_END") && $0.contains("device=\(mouseID)") && $0.contains("result=localLinkClosed") && $0.contains("remoteArrival=unobserved") })
        await engine.stop()
    }

    @Test func eitherSelectedKeyboardSwitchesAllSelectedMice() async throws {
        let names = ["keyboard-a", "keyboard-b", "mouse-a", "mouse-b"]
        let driver = DiscoveryDriver(endpoints: names.map { discoveryEndpoint($0, physicalID: $0, receiver: false, reportIDs: [0x11]) }, responsiveSlots: [0xFF])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        await engine.configure(keyboardIDs: ["keyboard-a#255", "keyboard-b#255"], mouseIDs: ["mouse-a#255", "mouse-b#255"], followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().filter(\.online).count == 4 })
        await driver.injectReport(deviceID: "keyboard-b", reportID: 0x11, bytes: [0x11, 0xFF, 9, 0, 2, 1] + Array(repeating: 0, count: 14))
        #expect(await discoveryEventually { Set(await driver.hostWriteEndpoints) == ["mouse-a", "mouse-b"] })
        #expect(await driver.hostWriteTargets(deviceID: "mouse-a") == [1])
        #expect(await driver.hostWriteTargets(deviceID: "mouse-b") == [1])
        await engine.stop()
    }

    @Test func followEnablesOnlyWirelessNotificationBitAndVerifiesItOnce() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("receiver")], receiverFlags: [0xA5, 0x28, 0x5A])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardIDs: [], mouseIDs: [], followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("수신기 무선 알림 확인:") } })
        #expect(await driver.notificationFlags == [0xA5, 0x29, 0x5A])
        await engine.refresh(); await engine.refresh()
        #expect(await driver.notificationWriteCount == 1)
        await engine.stop()
    }

    @Test func readOnlyDiscoveryDoesNotChangeReceiverFlags() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("receiver")], receiverFlags: [0xA5, 0x28, 0x5A])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        #expect(await driver.notificationWriteCount == 0)
        #expect(await driver.notificationFlags == [0xA5, 0x28, 0x5A])
        await engine.stop()
    }

    @Test func receiverWithWirelessAlreadyEnabledNeedsNoConfigurationWrite() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("receiver")], receiverFlags: [0xA5, 0x29, 0x5A])
        let log = try discoveryLog(), engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardIDs: [], mouseIDs: [], followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("수신기 무선 알림 확인:") } })
        #expect(await driver.notificationWriteCount == 0)
        await engine.stop()
    }

    @Test func initialConnectionReadsFeatureHostNameAndKind() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [1, 2])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        try await engine.start { _ in }
        let ready = await discoveryEventually {
            let devices = await engine.currentDevices()
            return devices.count == 2 && devices.allSatisfy { $0.online && $0.kind != "기기" }
        }
        #expect(ready)
        let devices = await engine.currentDevices()
        #expect(devices.map(\.name).sorted() == ["MX KEYS S", "MX Master 3S"].sorted())
        #expect(Set(devices.map(\.kind)) == ["키보드", "마우스"])
        #expect(devices.allSatisfy { $0.featureIndex == 9 && $0.currentHost == 0 && $0.hostCount == 3 })
        await engine.stop()
    }

    @Test func nameLookupThatRepeatsTheChangeHostIndexNeverSendsGetNameThere() async throws {
        // getName is function 1 like setCurrentHost, so reading the name through the 0x1814 index would write channel 1.
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])],
                                     responsiveSlots: [0xFF])
        await driver.useChangeHostIndex(slot: 0xFF, index: 3)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("이름 기능 위치 응답이 0x1814 위치") } })
        #expect(await driver.hostWriteCount == 0)
        await engine.stop()
    }

    @Test func expiredFollowWaitForAnUnreachableMouseIsUnconfirmedNotFailed() async throws {
        // Windows 0.2.43: a mouse that never answered may already be on the destination, and no command was sent.
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("receiver")], responsiveSlots: [1])
        let events = DiscoveryEvents()
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        await engine.configure(keyboardID: "receiver#1", mouseID: "receiver#2", followEnabled: true)
        try await engine.start { event in Task { await events.record(event) } }
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.id == "receiver#1" && $0.online } })
        await driver.injectReport(deviceID: "receiver", reportID: 0x11, bytes: [0x11, 1, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
        #expect(await discoveryEventually(attempts: 13_000) { await events.switchStates("receiver#2").contains(.unconfirmed) })
        let states = await events.switchStates("receiver#2")
        #expect(!states.contains(.failed))
        #expect(await driver.hostWriteCount == 0)
        await engine.stop()
    }

    @Test func departureParkedBeforeTheLookupIsFollowedWhenTheBluetoothHandleClosesWithARememberedIndex() async throws {
        // Measured on Windows 2026-10-24: the departure arrived on a freshly opened handle and was dropped at close.
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.online && $0.featureIndex == 9 } })
        await driver.remove("keyboard")
        await driver.changeFeatureIndex(slot: 0xFF, index: 9)
        await driver.add(keyboard)
        await driver.injectReport(deviceID: "keyboard", reportID: 0x11, bytes: [0x11, 0xFF, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
        try await Task.sleep(for: .milliseconds(200))
        await driver.remove("keyboard")
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("보관한 전환 신호를 출발로 처리함: HID 통로가 닫힘") && $0.contains("채널 2") } })
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|F2|K1|KEY|") } })
        await engine.stop()
    }

    @Test func departureParkedBeforeAnyLookupIsDiscardedWithItsReasonWhenTheIndexIsUnknown() async throws {
        let keyboard = discoveryEndpoint("keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        await driver.changeFeatureIndex(slot: 0xFF, index: 9)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        await driver.injectReport(deviceID: "keyboard", reportID: 0x11, bytes: [0x11, 0xFF, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
        try await Task.sleep(for: .milliseconds(200))
        await driver.remove("keyboard")
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("보관 이벤트 폐기: HID 통로가 닫힘") && $0.contains("0x1814 위치를 아직 모름") } })
        #expect(await log.recentCodes().allSatisfy { !$0.contains("|KEY|") })
        await engine.stop()
    }

    @Test func receiverLinkLostSettlesADepartureParkedBeforeTheLookup() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [1])
        await driver.changeFeatureIndex(slot: 1, index: 9)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "receiver#1", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        await driver.injectReport(deviceID: "bolt", reportID: 0x11, bytes: [0x11, 1, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
        try await Task.sleep(for: .milliseconds(200))
        await driver.injectReport(deviceID: "bolt", reportID: 0x10, bytes: [0x10, 1, 0x41, 0x10, 0x41, 0x34, 0xB0])
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("보관 이벤트 폐기: 수신기가 링크 끊김을 알림") } })
        await engine.stop()
    }

    @Test func aMouseWaitingForTheKeyboardIsQueriedAtOnceInsteadOfAtItsNextBackoff() async throws {
        // Slot 2 answered "empty slot" (error 9), which backs its refresh off by 30 s. When the keyboard leaves,
        // the waiting mouse must be asked again immediately, and it follows within the 10 s wait.
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [1, 2])
        await driver.reject(slot: 2)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "receiver#1", mouseID: "receiver#2", followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await log.recentLines().contains { $0.contains("30초 뒤 재조회") } })
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.id == "receiver#1" && $0.online } })
        await driver.accept(slot: 2)
        await driver.injectReport(deviceID: "bolt", reportID: 0x11, bytes: [0x11, 1, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
        #expect(await discoveryEventually(attempts: 6_000) { await driver.hostWriteTargets(deviceID: "bolt") == [1] })
        await engine.stop()
    }

    @Test func longReportWithFeatureIndex41NeverChangesAReceiverSlotLink() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [1])
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.id == "receiver#1" && $0.online } })
        await driver.injectReport(deviceID: "bolt", reportID: 0x11, bytes: [0x11, 1, 0x41, 0x00, 0x41, 0x42, 0xB0] + Array(repeating: 0, count: 13))
        try await Task.sleep(for: .milliseconds(200))
        #expect(await engine.currentDevices().contains { $0.id == "receiver#1" && $0.online })
        #expect(await log.recentLines().allSatisfy { !$0.contains("수신기 기기 연결") })
        await engine.stop()
    }

    @Test func anErrorReplyToOurSetCurrentHostIsLoggedWithItsDelayAndLayout() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [1, 2])
        await driver.answerHostWrites(withError: 5)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "receiver#1", mouseID: "receiver#2", followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().filter(\.online).count == 2 })
        await driver.injectReport(deviceID: "bolt", reportID: 0x11, bytes: [0x11, 1, 9, 0, 0, 1] + Array(repeating: 0, count: 14))
        #expect(await discoveryEventually {
            await log.recentLines().contains { $0.contains("보낸 setCurrentHost에 기기가 오류로 응답함") && $0.contains("오류 코드 5") && $0.contains("ErrorSolaarLayout") }
        })
        await engine.stop()
    }

    @Test func shortAndLongCollectionsOfOneReceiverDoNotDuplicateDevices() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("short", reportIDs: [0x10]),
                                                 discoveryEndpoint("long", reportIDs: [0x11])])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        try await engine.start { _ in }
        #expect(await discoveryEventually {
            let devices = await engine.currentDevices()
            return devices.count == 1 && devices.first?.kind == "키보드"
        })
        let devices = await engine.currentDevices()
        #expect(devices.count == 1)
        #expect(devices.first?.id == "receiver#1")
        #expect(await driver.endpointIDsUsed.contains("long"))
        await engine.stop()
    }

    @Test func removalAndNewCollectionIDsRestoreSamePhysicalDevice() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("old-interface")])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        let originalID = await engine.currentDevices().first?.id
        let before = await engine.switchSnapshot("receiver#1")?.generation
        await driver.remove("old-interface")
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == false })
        await driver.add(discoveryEndpoint("new-interface"))
        #expect(await discoveryEventually {
            let devices = await engine.currentDevices()
            return devices.count == 1 && devices.first?.online == true && devices.first?.kind == "키보드"
        })
        let devices = await engine.currentDevices()
        let after = await engine.switchSnapshot("receiver#1")?.generation
        #expect(devices.first?.id == originalID)
        #expect(before != after)
        #expect(await driver.endpointIDsUsed.contains("new-interface"))
        await engine.stop()
    }

    @Test func unresponsiveSlotsRemainUnidentifiedInsteadOfVisibleOfflineDevices() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        try await engine.start { _ in }
        #expect(await discoveryEventually { await driver.sendCount(slot: 6) > 0 })
        #expect(await engine.currentDevices().isEmpty)
        await engine.stop()
    }

    @Test func bluetoothUsesDirectIndexRatherThanReceiverSlots() async throws {
        let endpoint = discoveryEndpoint("bluetooth", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [endpoint], responsiveSlots: [0xFF])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().first?.kind == "키보드" })
        let devices = await engine.currentDevices()
        #expect(devices.count == 1)
        #expect(devices.first?.id == "keyboard#255")
        #expect(devices.first?.connection == "Bluetooth")
        #expect(await driver.sendCount(slot: 1) == 0)
        await engine.stop()
    }

    @Test func missingInitialHostReplyRetriesPromptlyAfterTimeout() async throws {
        let keyboard = discoveryEndpoint("fake-keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        await driver.ignoreNextHostInfo(deviceID: keyboard.id)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        try await engine.start { _ in }
        #expect(await discoveryEventually(attempts: 2_000) { await log.recentLines().contains { $0.contains("조회 미완료: timeout") } })
        let retryStarted = ContinuousClock.now
        #expect(await discoveryEventually { await driver.hostInfoRequestCount(deviceID: keyboard.id) >= 2 })
        #expect(retryStarted.duration(to: .now) < .milliseconds(800))
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        await engine.stop()
    }

    @Test(arguments: ["P", "B"])
    func manualSwitchWaitsForAttachedKeyboardHostInfoWithoutDelayingReadyMouse(_ source: String) async throws {
        let keyboard = discoveryEndpoint("fake-keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let mouse = discoveryEndpoint("fake-mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard, mouse], responsiveSlots: [0xFF])
        await driver.holdHostInfo(deviceID: keyboard.id)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: "mouse#255", followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await driver.heldHostInfoCount == 1 })
        #expect(await discoveryEventually { await engine.currentDevices().contains { $0.id == "mouse#255" && $0.online } })
        let request = Task { await engine.switchChannel(2, source: source) }
        #expect(await discoveryEventually { await driver.hostWriteEndpoints == [mouse.id] })
        await driver.releaseHostInfo()
        #expect(await discoveryEventually { await driver.hostWriteEndpoints.contains(keyboard.id) })
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [1])
        #expect(await log.recentLines().contains { $0.contains("수동 전환 대기:") })
        await engine.stop()
        await request.value
    }

    @Test func newerManualDestinationReplacesKeyboardRequestWaitingForHostInfo() async throws {
        let keyboard = discoveryEndpoint("fake-keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        await driver.holdHostInfo(deviceID: keyboard.id)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await driver.heldHostInfoCount == 1 })
        let first = Task { await engine.switchChannel(2, source: "P") }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|R0001|") && $0.contains("|WAIT|") } })
        let second = Task { await engine.switchChannel(3, source: "P") }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|R0002|") && $0.contains("|WAIT|") } })
        await driver.releaseHostInfo()
        #expect(await discoveryEventually { await driver.hostWriteCount == 1 })
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [2])
        await engine.stop()
        await first.value; await second.value
    }

    @Test func keyboardReattachmentCannotReplayAnOlderManualRequest() async throws {
        let keyboard = discoveryEndpoint("fake-keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        await driver.holdHostInfo(deviceID: keyboard.id)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await driver.heldHostInfoCount == 1 })
        let request = Task { await engine.switchChannel(2, source: "P") }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|WAIT|") } })
        await driver.remove(keyboard.id)
        let returned = discoveryEndpoint("fake-keyboard-returned", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        await driver.add(returned)
        await driver.releaseHostInfo()
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        await request.value
        #expect(await driver.hostWriteCount == 0)
        #expect(await log.recentCodes().contains { $0.contains("|CAN_PREP|") })
        await engine.stop()
    }

    @Test func selectionChangeCancelsManualRequestWaitingForHostInfo() async throws {
        let keyboard = discoveryEndpoint("fake-keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        await driver.holdHostInfo(deviceID: keyboard.id)
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await driver.heldHostInfoCount == 1 })
        let request = Task { await engine.switchChannel(2, source: "P") }
        #expect(await discoveryEventually { await log.recentCodes().contains { $0.contains("|WAIT|") } })
        await engine.configure(keyboardID: nil, mouseID: nil, followEnabled: true)
        await driver.releaseHostInfo()
        await request.value
        #expect(await driver.hostWriteCount == 0)
        await engine.stop()
    }

    @Test(arguments: [true, false])
    func serialIdentifiedBluetoothReconnectionNotifiesWithoutRepeatingMetadata(_ stableIdentity: Bool) async throws {
        let serial = stableIdentity ? "keyboard-serial" : nil
        let first = discoveryEndpoint("fake-keyboard-first", physicalID: "keyboard", receiver: false,
                                      reportIDs: [0x11], serialNumber: serial)
        let driver = DiscoveryDriver(endpoints: [first], responsiveSlots: [0xFF])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        let events = DiscoveryEvents()
        try await engine.start { event in Task { await events.record(event) } }
        #expect(await discoveryEventually { await events.connectionNoticeCount == 1 })
        let metadataBeforeReturn = await driver.metadataRequestCount
        await driver.remove(first.id)
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == false })
        await driver.suppressMetadataReplies()
        let returned = discoveryEndpoint("fake-keyboard-returned", physicalID: "keyboard", receiver: false,
                                         reportIDs: [0x11], serialNumber: serial)
        await driver.holdHostInfo(deviceID: returned.id)
        await driver.add(returned)
        #expect(await discoveryEventually { await driver.heldHostInfoCount > 0 })
        // Re-enumeration changes the route generation, but does not prove a live link.
        #expect(await engine.currentDevices().first?.online == false)
        #expect(await events.connectionNoticeCount == 1)
        await driver.releaseHostInfo()
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        if stableIdentity {
            #expect(await discoveryEventually { await events.connectionNoticeCount == 2 })
            #expect(await driver.metadataRequestCount == metadataBeforeReturn)
        } else {
            #expect(await discoveryEventually { await driver.metadataRequestCount > metadataBeforeReturn })
            #expect(await events.connectionNoticeCount == 1)
        }
        #expect(await driver.hostWriteCount == 0)
        await engine.stop()
    }

    @Test func manualSwitchPresentsItsInitialNoticeBeforeWritingEitherDevice() async throws {
        let keyboard = discoveryEndpoint("fake-keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let mouse = discoveryEndpoint("fake-mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard, mouse], responsiveSlots: [0xFF])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        await engine.configure(keyboardID: "keyboard#255", mouseID: "mouse#255", followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually {
            let devices = await engine.currentDevices()
            return devices.count == 2 && devices.allSatisfy(\.online)
        })
        let gate = ManualNoticeGate()
        let request = Task { await engine.switchChannel(2, source: "P", presentNotice: { _ in await gate.present() }) }
        #expect(await discoveryEventually { await gate.entered })
        #expect(await driver.hostWriteCount == 0)
        await gate.release()
        #expect(await discoveryEventually { await driver.hostWriteCount == 2 })
        await engine.stop(); await request.value
    }

    @Test func destinationChangeDuringInitialNoticeCannotSendTheOldDestination() async throws {
        let keyboard = discoveryEndpoint("fake-keyboard", physicalID: "keyboard", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [keyboard], responsiveSlots: [0xFF])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        await engine.configure(keyboardID: "keyboard#255", mouseID: nil, followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().first?.online == true })
        let gate = ManualNoticeGate()
        let old = Task { await engine.switchChannel(2, source: "P", presentNotice: { _ in await gate.present() }) }
        #expect(await discoveryEventually { await gate.entered })
        let latest = Task { await engine.switchChannel(3, source: "P") }
        #expect(await discoveryEventually { await driver.hostWriteCount == 1 })
        await gate.release(); await old.value
        #expect(await driver.hostWriteTargets(deviceID: keyboard.id) == [2])
        await engine.stop(); await latest.value
    }

    @Test func receiverSlotReplacementInvalidatesSelectionAndRequeriesFeatureBeforeWriting() async throws {
        let driver = DiscoveryDriver(endpoints: [discoveryEndpoint("bolt")], responsiveSlots: [2])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        let events = DiscoveryEvents()
        try await engine.start { event in Task { await events.record(event) } }
        #expect(await discoveryEventually { await engine.currentDevices().first?.kind == "마우스" })
        await engine.configure(keyboardID: nil, mouseID: "receiver#2", followEnabled: false)
        await driver.connectionNotice(deviceID: "bolt", slot: 2, productID: 0xB034)
        // A same-link initial notice records the identity; the next different identity replaces that slot.
        try await Task.sleep(for: .milliseconds(10))
        let before = await driver.featureLookupCount
        await driver.changeFeatureIndex(slot: 2, index: 12)
        await driver.connectionNotice(deviceID: "bolt", slot: 2, productID: 0xB042)
        #expect(await discoveryEventually { await events.containsInvalidation("receiver#2") })
        #expect(await discoveryEventually { await driver.featureLookupCount > before })
        #expect(await engine.currentDevices().isEmpty)
        do {
            try await engine.writeHost("receiver#2", target: 1)
            Issue.record("Cached index must not send a host command to a replacement device")
        } catch EngineError.disconnected {} catch { Issue.record("Wrong error: \(error)") }
        #expect(await driver.hostWriteCount == 0)

        await driver.releaseFeatureLookups()
        #expect(await discoveryEventually {
            let devices = await engine.currentDevices()
            return devices.first?.featureIndex == 12 && devices.first?.kind == "마우스" && devices.first?.online == true
        })
        await engine.switchChannel(2)
        // The replaced physical slot is no longer the selected mouse, even after rediscovery succeeds.
        #expect(await driver.hostWriteCount == 0)
        await engine.stop()
    }

    @Test func actualVendorOpenPermissionDenialRequestsInputMonitoring() async throws {
        let denied = discoveryEndpoint("denied", physicalID: "keyboard", receiver: false, reportIDs: [0x11],
                                       isOpen: false, openResult: -536870174)
        let driver = DiscoveryDriver(endpoints: [denied])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        let events = PermissionEvents()
        try await engine.start { events.record($0) }
        #expect(await discoveryEventually { events.requestCount == 1 })
        #expect(await engine.currentDevices().isEmpty)
        #expect(await driver.sendCount(slot: 0xFF) == 0)
        await engine.stop()
    }

    @Test func genericKeyboardAndMouseReportsNeverEnterDiagnosticLogsWhileHidppStillWorks() async throws {
        let endpoint = discoveryEndpoint("synthetic-bluetooth", physicalID: "synthetic-keyboard", receiver: false, reportIDs: [1, 2, 0x11])
        let driver = DiscoveryDriver(endpoints: [endpoint], responsiveSlots: [0xFF])
        // This fresh, unique temporary log contains only synthetic test traffic.
        let log = try discoveryLog(traceFrames: true)
        let engine = DeviceEngine(driver: driver, log: log)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().first?.kind == "키보드" })

        let genericKeyboard: [UInt8] = [0x01, 0xEE, 0x5A, 0xC7, 0x91, 0x04]
        let genericMouse: [UInt8] = [0x02, 0xDA, 0x6B, 0xB8, 0x83, 0x05]
        await driver.injectReport(deviceID: endpoint.id, reportID: 1, bytes: genericKeyboard)
        await driver.injectReport(deviceID: endpoint.id, reportID: 2, bytes: genericMouse)

        // The matching HID++ response is enqueued after both generic events. Awaiting it
        // provides a stream-processing barrier without inspecting any real device log.
        let host = try await engine.readHost("synthetic-keyboard#255")
        #expect(host.hostCount == 3 && host.currentHost == 0)
        await log.flush()
        let lines = await log.recentLines()
        #expect(lines.contains { $0.contains("RX synthetic-bluetooth reportID=17 ") })
        #expect(!lines.contains { $0.contains(HidppHex.format(genericKeyboard)) || $0.contains(HidppHex.format(genericMouse)) })
        #expect(!lines.contains { $0.contains("reportID=1 ") || $0.contains("reportID=2 ") })
        #expect(!lines.contains { $0.contains("unsupported callback report ID") || $0.contains("리포트 정규화 생략") })
        await engine.stop()
    }

    @Test func restoredBluetoothSelectionsFollowDuringStartAndSurviveNewAttachmentIDsWithoutReselection() async throws {
        let keyboardID = "saved-keyboard#255", mouseID = "saved-mouse#255"
        for attachment in ["first", "replacement"] {
            let keyboardEndpoint = "fake-keyboard-\(attachment)", mouseEndpoint = "fake-mouse-\(attachment)"
            let keyboard = discoveryEndpoint(keyboardEndpoint, physicalID: "saved-keyboard", receiver: false, reportIDs: [0x11])
            let mouse = discoveryEndpoint(mouseEndpoint, physicalID: "saved-mouse", receiver: false, reportIDs: [0x11])
            let departure: [UInt8] = [0x11, 0xFF, 9, 0, 0, 1] + Array(repeating: 0, count: 14)
            let driver = DiscoveryDriver(endpoints: [keyboard, mouse], responsiveSlots: [0xFF], metadataReplies: false,
                                         initialReports: [(keyboardEndpoint, 0x11, departure)], pauseStart: true)
            let log = try discoveryLog(traceFrames: true)
            let engine = DeviceEngine(driver: driver, log: log)
            // This is restoration, not a user selecting devices after discovery.
            await engine.configure(keyboardID: keyboardID, mouseID: mouseID, followEnabled: true)
            let starting = Task { try await engine.start { _ in } }
            let followedWhileStarting = await discoveryEventually(attempts: 2_500) { await driver.hostWriteCount == 1 }
            await driver.finishStarting()
            try await starting.value
            #expect(followedWhileStarting)
            #expect(await driver.hostWriteEndpoints == [mouseEndpoint])
            #expect(await driver.metadataRequestCount > 0)
            let devices = await engine.currentDevices()
            #expect(devices.count == 2 && devices.allSatisfy { $0.kind == "기기" })
            let lines = await log.recentLines(), codes = await log.recentCodes()
            #expect(lines.contains { $0.contains("초기 자발 신호 보관") })
            #expect(codes.contains { $0.contains("|KEY|") })
            #expect(!codes.contains { $0.contains("SKIP_KEY") || $0.contains("SKIP_SEL") || $0.contains("SKIP_OFF") })
            await engine.stop()
        }
    }

    @Test func latestSelectedKeyboardControlsFollowAfterConfigurationChanges() async throws {
        let oldKeyboard = discoveryEndpoint("fake-old-keyboard", physicalID: "old-keyboard", receiver: false, reportIDs: [0x11])
        let latestKeyboard = discoveryEndpoint("fake-latest-keyboard", physicalID: "latest-keyboard", receiver: false, reportIDs: [0x11])
        let mouse = discoveryEndpoint("fake-mouse", physicalID: "mouse", receiver: false, reportIDs: [0x11])
        let driver = DiscoveryDriver(endpoints: [oldKeyboard, latestKeyboard, mouse], responsiveSlots: [0xFF])
        let log = try discoveryLog()
        let engine = DeviceEngine(driver: driver, log: log)
        await engine.configure(keyboardID: "old-keyboard#255", mouseID: "mouse#255", followEnabled: true)
        try await engine.start { _ in }
        #expect(await discoveryEventually { await engine.currentDevices().count == 3 })
        await engine.configure(keyboardID: nil, mouseID: nil, followEnabled: false)
        await engine.configure(keyboardID: "latest-keyboard#255", mouseID: "mouse#255", followEnabled: true)
        let departure: [UInt8] = [0x11, 0xFF, 9, 0, 0, 1] + Array(repeating: 0, count: 14)
        await driver.injectReport(deviceID: oldKeyboard.id, reportID: 0x11, bytes: departure)
        await driver.injectReport(deviceID: latestKeyboard.id, reportID: 0x11, bytes: departure)
        #expect(await discoveryEventually { await driver.hostWriteCount == 1 })
        #expect(await driver.hostWriteEndpoints == [mouse.id])
        let codes = await log.recentCodes()
        #expect(codes.filter { $0.contains("|SKIP_KEY|") }.count == 1)
        #expect(!codes.contains { $0.contains("|SKIP_OFF|") || $0.contains("|SKIP_SEL|") })
        await engine.stop()
    }

    @Test(arguments: NoPermissionScenario.allCases)
    private func doesNotInferPermissionRequirementWithoutVendorOpenDenial(_ scenario: NoPermissionScenario) async throws {
        var endpoints: [HIDDeviceDescriptor] = []
        switch scenario {
        case .noHardware: break
        case .nonVendorCollection:
            endpoints.append(discoveryEndpoint("generic-keyboard", isCandidate: false, isOpen: false, openResult: -536870174))
        case .successfulOpen: break
        case .unrelatedOpenFailure:
            endpoints.append(discoveryEndpoint("failed-vendor", isOpen: false, openResult: -1))
        case .noOpenResult:
            endpoints.append(discoveryEndpoint("unopened-vendor", isOpen: false, openResult: nil))
        }
        if scenario != .noHardware {
            // This last endpoint's full discovery is a barrier after the preceding added events.
            endpoints.append(discoveryEndpoint("marker", physicalID: "marker", receiver: false, reportIDs: [0x11]))
        }
        let driver = DiscoveryDriver(endpoints: endpoints, responsiveSlots: [0xFF])
        let engine = DeviceEngine(driver: driver, log: try discoveryLog())
        let events = PermissionEvents()
        try await engine.start { events.record($0) }
        if scenario == .noHardware {
            #expect(await discoveryEventually { events.eventCount > 0 })
        } else {
            #expect(await discoveryEventually {
                await engine.currentDevices().contains { $0.id == "marker#255" && $0.kind == "키보드" }
            })
        }
        #expect(events.requestCount == 0)
        await engine.stop()
    }
}
