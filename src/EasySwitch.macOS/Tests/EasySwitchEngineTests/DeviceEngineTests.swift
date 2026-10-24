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
    private var heldReplies: [(device: String, bytes: [UInt8])] = []
    private var heldHostInfoDevices: Set<String> = []
    private var heldHostReplies: [(device: String, bytes: [UInt8])] = []
    private var ignoredHostInfo: Set<String> = []
    private var hostWrites: [[UInt8]] = []
    private var hostWriteDevices: [String] = []
    private var metadataReplies: Bool
    private let initialReports: [(device: String, reportID: Int, bytes: [UInt8])]
    private let pauseStart: Bool
    private var startGate: CheckedContinuation<Void, Never>?

    init(endpoints: [HIDDeviceDescriptor], responsiveSlots: Set<UInt8> = [1], metadataReplies: Bool = true,
         initialReports: [(device: String, reportID: Int, bytes: [UInt8])] = [], pauseStart: Bool = false) {
        self.endpoints = endpoints
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
        guard bytes.count >= 7, responsiveSlots.contains(bytes[1]), endpoints.contains(where: { $0.id == deviceID }) else { return }
        var parameters: [UInt8]
        let slot = bytes[1]
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
    func record(_ event: EngineEvent) {
        if case .selectionInvalidated(let id) = event { invalidated.insert(id) }
        if case .notice(_, _, let rows, connection: true) = event { connected.append(contentsOf: rows) }
    }
    func containsInvalidation(_ id: String) -> Bool { invalidated.contains(id) }
    var connectionNoticeCount: Int { connected.count }
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
    func record(_ event: EngineEvent) {
        lock.lock()
        defer { lock.unlock() }
        events += 1
        if case .inputMonitoringRequired = event { permissionRequests += 1 }
    }
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

private func discoveryLog() throws -> FileLog {
    try FileLog(directory: FileManager.default.temporaryDirectory.appendingPathComponent("easyswitch-discovery-tests-\(UUID())"))
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
        await driver.add(returned)
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
        let log = try discoveryLog()
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
            let log = try discoveryLog()
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
