import Foundation
import IOKit
import IOKit.hid
import IOKit.hidsystem

protocol HIDWorkerSession: AnyObject, Sendable {
    func start()
    func requestStop()
    func stop() async
    func snapshot() async -> [HIDDeviceDescriptor]
    func send(deviceID: String, bytes: [UInt8]) async throws
}

struct HIDWorkerConfiguration: Sendable {
    let physicalID: String?
    let enumerationOnly: Bool
    let onEvent: @Sendable (HIDEvent) -> Void
    let started: @Sendable (Result<Void, Error>) -> Void
}

/// Discovery has its own run loop; each physical device owns another run loop for all of its I/O.
/// Separate Bluetooth devices never share a blocking write queue. Receiver collections share one owner.
/// An owner should call stop before dropping its final reference; deinit also requests cleanup.
public final class HIDMonitor: @unchecked Sendable {
    private let lock = NSLock()
    private let factory: @Sendable (HIDWorkerConfiguration) -> any HIDWorkerSession
    private var worker: (any HIDWorkerSession)?
    private var deviceWorkers: [String: any HIDWorkerSession] = [:]
    private var deviceOwners: [String: String] = [:]
    private var generation: UInt64 = 0

    public init() { factory = { HIDWorker(configuration: $0) } }
    init(factory: @escaping @Sendable (HIDWorkerConfiguration) -> any HIDWorkerSession) { self.factory = factory }

    deinit {
        worker?.requestStop()
        for device in deviceWorkers.values { device.requestStop() }
    }

    public static func inputMonitoringAccess() -> HIDAccessStatus {
        switch IOHIDCheckAccess(kIOHIDRequestTypeListenEvent) {
        case kIOHIDAccessTypeGranted: .granted
        case kIOHIDAccessTypeDenied: .denied
        default: .unknown
        }
    }

    /// Call only from an explicit user action. False is not proof that a later Settings grant failed;
    /// check inputMonitoringAccess again and restart the monitor after the user changes permission.
    /// IOHIDLib.h declares a synchronous C API without a thread restriction. MainActor keeps this
    /// user-facing request in the app's interaction path; device open/read/write still stay off it.
    @MainActor
    public static func requestInputMonitoringAccess() -> Bool {
        IOHIDRequestAccess(kIOHIDRequestTypeListenEvent)
    }

    public func start(onEvent: @escaping @Sendable (HIDEvent) -> Void) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let created: (any HIDWorkerSession)? = lock.withLock {
                guard worker == nil else { return nil }
                generation &+= 1
                let token = generation
                let configuration = HIDWorkerConfiguration(physicalID: nil, enumerationOnly: true,
                    onEvent: { [weak self] event in self?.discovered(event, generation: token, onEvent: onEvent) },
                    started: { continuation.resume(with: $0) })
                let created = factory(configuration)
                worker = created
                return created
            }
            guard let created else {
                continuation.resume(throwing: HIDTransportError.alreadyRunning)
                return
            }
            created.start()
        }
    }

    public func stop() async {
        let previous: [any HIDWorkerSession] = lock.withLock {
            let previous = (worker.map { [$0] } ?? []) + Array(deviceWorkers.values)
            generation &+= 1
            worker = nil
            deviceWorkers.removeAll(); deviceOwners.removeAll()
            return previous
        }
        for session in previous { session.requestStop() }
        await withTaskGroup(of: Void.self) { group in
            for session in previous { group.addTask { await session.stop() } }
        }
    }

    public func snapshot() async -> [HIDDeviceDescriptor] {
        let current = lock.withLock { (worker, Array(deviceWorkers.values)) }
        var devices = Dictionary((await current.0?.snapshot() ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        await withTaskGroup(of: [HIDDeviceDescriptor].self) { group in
            for session in current.1 { group.addTask { await session.snapshot() } }
            for await descriptors in group { for device in descriptors { devices[device.id] = device } }
        }
        return devices.values.sorted { $0.id < $1.id }
    }

    /// Sends canonical bytes including the report ID. This API never waits for a protocol response.
    public func send(deviceID: String, bytes: [UInt8]) async throws {
        let state = lock.withLock { (worker != nil, deviceOwners[deviceID].flatMap { deviceWorkers[$0] }) }
        guard state.0 else { throw HIDTransportError.stopped }
        guard let current = state.1 else { throw HIDTransportError.deviceUnavailable(deviceID) }
        try await current.send(deviceID: deviceID, bytes: bytes)
    }

    private func discovered(_ event: HIDEvent, generation token: UInt64, onEvent: @escaping @Sendable (HIDEvent) -> Void) {
        switch event {
        case .added(let descriptor):
            let created: (any HIDWorkerSession)? = lock.withLock {
                guard worker != nil, generation == token else { return nil }
                deviceOwners[descriptor.id] = descriptor.physicalID
                guard descriptor.isHidppCandidate, deviceWorkers[descriptor.physicalID] == nil else { return nil }
                let configuration = HIDWorkerConfiguration(physicalID: descriptor.physicalID, enumerationOnly: false,
                    onEvent: { [weak self] event in self?.received(event, generation: token, onEvent: onEvent) },
                    started: { result in
                        if case .failure(let error) = result { onEvent(.diagnostic("기기별 HID 감시 시작 실패: \(error)")) }
                    })
                let created = factory(configuration)
                deviceWorkers[descriptor.physicalID] = created
                return created
            }
            created?.start()
        case .removed(let id):
            lock.withLock { if worker != nil && generation == token { deviceOwners.removeValue(forKey: id) } }
        case .diagnostic:
            if lock.withLock({ worker != nil && generation == token }) { onEvent(event) }
        case .report: break
        }
    }

    private func received(_ event: HIDEvent, generation token: UInt64, onEvent: @escaping @Sendable (HIDEvent) -> Void) {
        let current = lock.withLock {
            guard worker != nil, generation == token else { return false }
            if case .added(let descriptor) = event { deviceOwners[descriptor.id] = descriptor.physicalID }
            if case .removed(let id) = event { deviceOwners.removeValue(forKey: id) }
            return true
        }
        if current { onEvent(event) }
    }
}

extension HIDTransportError {
    /// IOReturn.h: NotPermitted=0xE00002E2; NotPrivileged=0xE00002C1.
    /// An exclusive-open conflict is a different failure and must not be called a TCC denial.
    public static func isPermissionDenied(_ result: Int32) -> Bool {
        result == kIOReturnNotPermitted || result == kIOReturnNotPrivileged
    }
}

private final class HIDEntry {
    weak var owner: HIDWorker?
    let device: IOHIDDevice
    let descriptor: HIDDeviceDescriptor
    let traceToken: String?
    let traceRole: String
    let buffer: UnsafeMutablePointer<UInt8>
    let bufferSize: Int

    init(device: IOHIDDevice, descriptor: HIDDeviceDescriptor, owner: HIDWorker,
         traceToken: String?, traceRole: String) {
        self.owner = owner
        self.device = device
        self.descriptor = descriptor
        self.traceToken = traceToken
        self.traceRole = traceRole
        bufferSize = max(64, min(descriptor.maxInputReportSize, 65536))
        buffer = .allocate(capacity: bufferSize)
        buffer.initialize(repeating: 0, count: bufferSize)
    }

    deinit { buffer.deallocate() }
}

/// Mutable IOKit state is confined to run(), its callbacks, and perform() blocks on that same run loop.
private final class HIDWorker: HIDWorkerSession, @unchecked Sendable {
    private let lock = NSLock()
    private var runLoop: CFRunLoop?
    private var accepting = true
    private var stopWaiters: [CheckedContinuation<Void, Never>] = []
    private var finished = false
    private var shouldStop = false
    private var manager: IOHIDManager?
    private var entries: [String: HIDEntry] = [:]
    private var nextTraceToken: UInt64 = 0
    private var traceSequence: UInt64 = 0
    private let traceRun = String(UUID().uuidString.prefix(8))
    // Keep callback buffers alive through unscheduling and the current run-loop iteration.
    private var retired: [HIDEntry] = []
    private let onEvent: @Sendable (HIDEvent) -> Void
    private let started: @Sendable (Result<Void, Error>) -> Void
    private let physicalID: String?
    private let enumerationOnly: Bool

    init(configuration: HIDWorkerConfiguration) {
        onEvent = configuration.onEvent; started = configuration.started
        physicalID = configuration.physicalID; enumerationOnly = configuration.enumerationOnly
    }

    func start() {
        let thread = Thread { [self] in run() }
        thread.name = enumerationOnly ? "EasySwitch.IOHID.discovery" : "EasySwitch.IOHID.\(traceRun)"
        thread.qualityOfService = .userInitiated
        thread.start()
    }

    func run() {
        autoreleasepool {
            let loop = CFRunLoopGetCurrent()!
            // stop() may have arrived before the worker thread started.
            shouldStop = lock.withLock {
                runLoop = loop
                return !accepting
            }
            // IndependentDevices prevents manager open/schedule from opening keyboard/mouse collections.
            let newManager = IOHIDManagerCreate(kCFAllocatorDefault, IOHIDManagerOptions.independentDevices.rawValue)
            manager = newManager
            IOHIDManagerSetDeviceMatching(newManager, [kIOHIDVendorIDKey: 0x046D] as CFDictionary)
            let context = Unmanaged.passUnretained(self).toOpaque()
            IOHIDManagerRegisterDeviceMatchingCallback(newManager, { context, result, _, device in
                guard let context else { return }
                let worker = Unmanaged<HIDWorker>.fromOpaque(context).takeUnretainedValue()
                if result == kIOReturnSuccess { worker.add(device) }
                else { worker.onEvent(.diagnostic("Device matching callback failed: \(result)")) }
            }, context)
            IOHIDManagerRegisterDeviceRemovalCallback(newManager, { context, _, _, device in
                guard let context else { return }
                Unmanaged<HIDWorker>.fromOpaque(context).takeUnretainedValue().remove(device)
            }, context)
            IOHIDManagerScheduleWithRunLoop(newManager, loop, CFRunLoopMode.defaultMode.rawValue)
            let result = IOHIDManagerOpen(newManager, IOOptionBits(kIOHIDOptionsTypeNone))
            if result == kIOReturnSuccess {
                onEvent(.diagnostic("IOHID manager started (shared, independent devices), Input Monitoring=\(HIDMonitor.inputMonitoringAccess().rawValue)"))
                // Enumerate already-attached devices immediately; callback duplicates are ignored by registry ID.
                if let devices = IOHIDManagerCopyDevices(newManager) as? Set<IOHIDDevice> {
                    for device in devices { add(device) }
                }
                started(.success(()))
                while !shouldStop {
                    CFRunLoopRunInMode(.defaultMode, 0.25, false)
                    retired.removeAll()
                }
            } else {
                onEvent(.diagnostic("\(HIDTransportError.managerOpen(result))"))
                started(.failure(HIDTransportError.managerOpen(result)))
            }
            tearDown(loop: loop)
            let waiters = lock.withLock {
                accepting = false
                finished = true
                runLoop = nil
                let waiters = stopWaiters
                stopWaiters.removeAll()
                return waiters
            }
            for waiter in waiters { waiter.resume() }
        }
    }

    func requestStop() {
        let loop = lock.withLock { () -> CFRunLoop? in
            accepting = false
            return runLoop
        }
        if let loop {
            CFRunLoopPerformBlock(loop, CFRunLoopMode.defaultMode.rawValue) { [self] in shouldStop = true }
            CFRunLoopWakeUp(loop)
        }
    }

    func stop() async {
        await withCheckedContinuation { continuation in
            let done = lock.withLock {
                if finished { return true }
                stopWaiters.append(continuation)
                return false
            }
            if done { continuation.resume() }
            else { requestStop() }
        }
    }

    private func perform(_ action: @escaping @Sendable () -> Void) -> Bool {
        lock.withLock {
            guard accepting, let runLoop, !finished else { return false }
            CFRunLoopPerformBlock(runLoop, CFRunLoopMode.defaultMode.rawValue, action)
            CFRunLoopWakeUp(runLoop)
            return true
        }
    }

    func snapshot() async -> [HIDDeviceDescriptor] {
        await withCheckedContinuation { continuation in
            if !perform({ [self] in
                continuation.resume(returning: entries.values.map(\.descriptor).sorted { $0.id < $1.id })
            }) { continuation.resume(returning: []) }
        }
    }

    func send(deviceID: String, bytes: [UInt8]) async throws {
        let enqueuedAt = Self.monotonicMS
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            if !perform({ [self] in
                do {
                    guard let entry = entries[deviceID], entry.descriptor.isOpen else {
                        throw HIDTransportError.deviceUnavailable(deviceID)
                    }
                    guard let reportID = bytes.first, reportID == 0x10 || reportID == 0x11,
                          bytes.count == (reportID == 0x10 ? 7 : 20) else {
                        throw HIDTransportError.invalidReport("send requires canonical 7/20-byte frame")
                    }
                    let ids = entry.descriptor.supportedReportIDs
                    guard ids.isEmpty || ids.contains(Int(reportID)) else {
                        throw HIDTransportError.invalidReport("report ID \(reportID) is absent from descriptor")
                    }
                    // Apple's contract includes reportID in both the argument and buffer for numbered reports.
                    let beganAt = Self.monotonicMS
                    trace("WRITE_BEGIN", entry: entry, at: beganAt,
                          details: "queueMs=\(Self.ms(beganAt - enqueuedAt)) rid=\(reportID) len=\(bytes.count) header=\(bytes.prefix(4).map { String(format: "%02X", $0) }.joined())")
                    let result = bytes.withUnsafeBufferPointer {
                        IOHIDDeviceSetReport(entry.device, kIOHIDReportTypeOutput, Int(reportID), $0.baseAddress!, bytes.count)
                    }
                    let endedAt = Self.monotonicMS
                    trace("WRITE_END", entry: entry, at: endedAt,
                          details: "durationMs=\(Self.ms(endedAt - beganAt)) result=\(Self.resultCode(result))")
                    guard result == kIOReturnSuccess else { throw HIDTransportError.writeFailed(result) }
                    continuation.resume()
                } catch { continuation.resume(throwing: error) }
            }) { continuation.resume(throwing: HIDTransportError.stopped) }
        }
    }

    private func add(_ device: IOHIDDevice) {
        guard !shouldStop else { return }
        let id = registryID(device)
        guard entries[id] == nil else { return }
        let pairs = usagePairs(device)
        let vendor = integer(device, kIOHIDVendorIDKey)
        let product = integer(device, kIOHIDProductIDKey)
        let location = integer(device, kIOHIDLocationIDKey)
        let serial = string(device, kIOHIDSerialNumberKey)
        let transport = string(device, kIOHIDTransportKey) ?? "Unknown"
        let physicalID = HIDDeviceIdentity.physicalID(registryID: id, vendorID: vendor, productID: product,
                                                     transport: transport, locationID: location, serialNumber: serial)
        guard self.physicalID == nil || self.physicalID == physicalID else { return }
        let candidate = HIDReportNormalizer.isHidppCandidate(vendorID: vendor, usagePairs: pairs)
        let traceToken: String?
        if candidate && !enumerationOnly { nextTraceToken &+= 1; traceToken = "C\(nextTraceToken)" }
        else { traceToken = nil }
        let traceRole = pairs.contains { $0.page == 0xFF00 && ($0.usage == 1 || $0.usage == 2) } ? "receiver"
            : pairs.contains { $0.page == 1 && $0.usage == 6 } ? "keyboard"
            : pairs.contains { $0.page == 1 && $0.usage == 2 } ? "mouse" : "vendor"
        let openBeganAt = Self.monotonicMS
        trace("OPEN_BEGIN", token: traceToken, role: traceRole, at: openBeganAt, details: "shared=1")
        let result = candidate && !enumerationOnly ? IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone)) : nil
        let openEndedAt = Self.monotonicMS
        trace("OPEN_END", token: traceToken, role: traceRole, at: openEndedAt,
              details: "durationMs=\(Self.ms(openEndedAt - openBeganAt)) result=\(result.map(Self.resultCode) ?? "not-opened")")
        let isOpen = result == kIOReturnSuccess
        let rawDescriptor = IOHIDDeviceGetProperty(device, kIOHIDReportDescriptorKey as CFString) as? Data
        let descriptor = HIDDeviceDescriptor(
            id: id, physicalID: physicalID,
            name: string(device, kIOHIDProductKey) ?? "Logitech HID", vendorID: vendor, productID: product,
            transport: transport, usagePairs: pairs,
            maxInputReportSize: integer(device, kIOHIDMaxInputReportSizeKey),
            maxOutputReportSize: integer(device, kIOHIDMaxOutputReportSizeKey),
            locationID: location, serialNumber: serial,
            supportedReportIDs: HIDReportNormalizer.reportIDs(in: rawDescriptor.map(Array.init) ?? []),
            isReceiver: pairs.contains { $0.page == 0xFF00 && ($0.usage == 1 || $0.usage == 2) },
            isHidppCandidate: candidate, isOpen: isOpen, openResult: result)
        let entry = HIDEntry(device: device, descriptor: descriptor, owner: self, traceToken: traceToken, traceRole: traceRole)
        entries[id] = entry
        if isOpen, let runLoop {
            IOHIDDeviceRegisterInputReportCallback(device, entry.buffer, entry.bufferSize, { context, result, _, _, reportID, bytes, length in
                // One macOS HID device can expose vendor and ordinary keyboard/mouse reports
                // together. Discard ordinary input at this native callback boundary, before
                // copying bytes, producing an event, logging, or collecting any input counts.
                guard reportID == 0x10 || reportID == 0x11 else { return }
                guard let context else { return }
                let entry = Unmanaged<HIDEntry>.fromOpaque(context).takeUnretainedValue()
                guard let worker = entry.owner else { return }
                let arrivedAt = HIDWorker.monotonicMS
                // Only vendor reports reach this point. Read at most four header bytes directly;
                // tracing never copies another payload or observes ordinary input reports.
                let header = result == kIOReturnSuccess
                    ? HIDWorker.vendorHeader(bytes, reportID: reportID, length: length, capacity: entry.bufferSize)
                    : "unavailable"
                let fields = "rid=\(reportID) len=\(length) header=\(header) result=\(HIDWorker.resultCode(result))"
                guard !worker.shouldStop else {
                    worker.trace("VENDOR_DROP", entry: entry, at: arrivedAt, details: "reason=stopping \(fields)")
                    return
                }
                guard worker.entries[entry.descriptor.id] === entry else {
                    worker.trace("VENDOR_DROP", entry: entry, at: arrivedAt, details: "reason=retired-entry \(fields)")
                    return
                }
                worker.trace("VENDOR_CB", entry: entry, at: arrivedAt, details: fields)
                if result != kIOReturnSuccess {
                    worker.onEvent(.diagnostic("Input report callback failed: \(result)"))
                    return
                }
                guard entry.descriptor.isOpen, length >= 0, length <= entry.bufferSize else { return }
                // The callback-owned bytes must be copied before this callback returns.
                worker.onEvent(.report(deviceID: entry.descriptor.id, reportID: Int(reportID), bytes: Array(UnsafeBufferPointer(start: bytes, count: length))))
            }, Unmanaged.passUnretained(entry).toOpaque())
            IOHIDDeviceScheduleWithRunLoop(device, runLoop, CFRunLoopMode.defaultMode.rawValue)
            trace("CB_READY", entry: entry, at: Self.monotonicMS, details: "vendorFilter=10,11")
        }
        onEvent(.added(descriptor))
    }

    private func remove(_ device: IOHIDDevice) {
        let id = registryID(device)
        if let entry = entries[id] { trace("REMOVE_BEGIN", entry: entry, at: Self.monotonicMS) }
        guard let entry = entries.removeValue(forKey: id) else { return }
        close(entry)
        retired.append(entry)
        trace("REMOVE_EVENT", entry: entry, at: Self.monotonicMS)
        onEvent(.removed(id))
    }

    private func close(_ entry: HIDEntry) {
        guard entry.descriptor.isOpen else { return }
        trace("UNREGISTER_BEGIN", entry: entry, at: Self.monotonicMS)
        IOHIDDeviceRegisterInputReportCallback(entry.device, entry.buffer, entry.bufferSize, nil, nil)
        if let runLoop { IOHIDDeviceUnscheduleFromRunLoop(entry.device, runLoop, CFRunLoopMode.defaultMode.rawValue) }
        trace("UNREGISTER_END", entry: entry, at: Self.monotonicMS)
        let closeBeganAt = Self.monotonicMS
        trace("CLOSE_BEGIN", entry: entry, at: closeBeganAt)
        let result = IOHIDDeviceClose(entry.device, IOOptionBits(kIOHIDOptionsTypeNone))
        let closeEndedAt = Self.monotonicMS
        trace("CLOSE_END", entry: entry, at: closeEndedAt,
              details: "durationMs=\(Self.ms(closeEndedAt - closeBeganAt)) result=\(Self.resultCode(result))")
    }

    private static var monotonicMS: Double { Double(DispatchTime.now().uptimeNanoseconds) / 1_000_000 }
    private static func ms(_ value: Double) -> String { String(format: "%.3f", locale: Locale(identifier: "en_US_POSIX"), value) }
    private static func resultCode(_ value: Int32) -> String { String(format: "%08X", UInt32(bitPattern: value)) }
    private static func vendorHeader(_ bytes: UnsafeMutablePointer<UInt8>, reportID: UInt32, length: Int, capacity: Int) -> String {
        guard length > 0, length <= capacity else { return "unavailable" }
        let includedID = bytes[0] == UInt8(reportID)
        let count = min(includedID ? 4 : 3, length)
        let prefix = includedID ? "" : String(format: "%02X", reportID)
        return prefix + (0..<count).map { String(format: "%02X", bytes[$0]) }.joined()
    }
    private func trace(_ stage: String, entry: HIDEntry, at: Double, details: String = "") {
        trace(stage, token: entry.traceToken, role: entry.traceRole, at: at, details: details)
    }
    private func trace(_ stage: String, token: String?, role: String, at: Double, details: String = "") {
        guard let token else { return }
        traceSequence &+= 1
        onEvent(.diagnostic("HIDTRACE run=\(traceRun) seq=\(traceSequence) connection=\(token) role=\(role) stage=\(stage) nativeMonoMs=\(Self.ms(at)) \(details)"))
    }

    private func tearDown(loop: CFRunLoop) {
        if let manager {
            IOHIDManagerRegisterDeviceMatchingCallback(manager, nil, nil)
            IOHIDManagerRegisterDeviceRemovalCallback(manager, nil, nil)
            IOHIDManagerUnscheduleFromRunLoop(manager, loop, CFRunLoopMode.defaultMode.rawValue)
        }
        for entry in entries.values { close(entry) }
        if let manager { IOHIDManagerClose(manager, IOOptionBits(kIOHIDOptionsTypeNone)) }
        manager = nil
        entries.removeAll()
        retired.removeAll()
        onEvent(.diagnostic("IOHID manager stopped"))
    }

    private func registryID(_ device: IOHIDDevice) -> String {
        var value: UInt64 = 0
        let result = IORegistryEntryGetRegistryEntryID(IOHIDDeviceGetService(device), &value)
        if result == kIOReturnSuccess { return "iohid:\(value)" }
        return "iohid-object:\(UInt(bitPattern: Unmanaged.passUnretained(device).toOpaque()))"
    }

    private func integer(_ device: IOHIDDevice, _ key: String) -> Int {
        (IOHIDDeviceGetProperty(device, key as CFString) as? NSNumber)?.intValue ?? 0
    }

    private func string(_ device: IOHIDDevice, _ key: String) -> String? {
        IOHIDDeviceGetProperty(device, key as CFString) as? String
    }

    private func usagePairs(_ device: IOHIDDevice) -> [HIDUsagePair] {
        let raw = IOHIDDeviceGetProperty(device, kIOHIDDeviceUsagePairsKey as CFString) as? [[String: Any]] ?? []
        var pairs = raw.compactMap { item -> HIDUsagePair? in
            guard let page = item[kIOHIDDeviceUsagePageKey] as? NSNumber,
                  let usage = item[kIOHIDDeviceUsageKey] as? NSNumber else { return nil }
            return HIDUsagePair(page: page.intValue, usage: usage.intValue)
        }
        if pairs.isEmpty {
            pairs.append(HIDUsagePair(page: integer(device, kIOHIDPrimaryUsagePageKey), usage: integer(device, kIOHIDPrimaryUsageKey)))
        }
        return Array(Set(pairs)).sorted { ($0.page, $0.usage) < ($1.page, $1.usage) }
    }
}
