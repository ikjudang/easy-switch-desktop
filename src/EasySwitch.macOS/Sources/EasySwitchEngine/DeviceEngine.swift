import Foundation
import EasySwitchCore
import EasySwitchTransport

public struct DeviceState: Identifiable, Codable, Sendable, Equatable {
    public var id: String
    public var name: String
    public var connection: String
    public var kind: String
    public var online: Bool
    public var currentHost: Int?
    public var hostCount: Int
    public var featureIndex: Int
    public init(id: String, name: String, connection: String, kind: String, online: Bool,
                currentHost: Int?, hostCount: Int, featureIndex: Int) {
        self.id = id; self.name = name; self.connection = connection; self.kind = kind
        self.online = online; self.currentHost = currentHost; self.hostCount = hostCount; self.featureIndex = featureIndex
    }
}

public struct ReceiverState: Identifiable, Codable, Sendable, Equatable {
    public let id: String
    public let name: String
    public let bolt: Bool
}

public enum EngineEvent: Codable, Sendable {
    case devices([DeviceState]), scanning(Bool), status(String), selectionInvalidated(String), inputMonitoringRequired
    case notice(run: Int, target: Int, rows: [NoticeRow], connection: Bool)
    case keyboardDeparture(target: Int)
    case keyboardArrival(id: String, channel: Int)
    case receivers([ReceiverState]), pairing(ReceiverPairingProgress)
}

public actor DeviceEngine: DeviceSwitching {
    private struct Route {
        let endpoint: String
        let index: Int
        var generation: UInt64
        var revision: UInt64 = 0
        var localLink: Bool?
        var observedLocally = false
        var departing = false
        var departedAt: Int64 = 0
        var metadataRead = false
        var wirelessProductID: Int?
        var firstSeenAt: Int64 = Int64(ProcessInfo.processInfo.systemUptime * 1000)
        var nextRefreshAt: Int64 = 0
        var state: DeviceState
        var early = EarlySignalGate<HidppFrame>()
    }
    private let driver: any HIDDriving
    private let log: FileLog
    private let router: RequestRouter
    private var endpoints: [String: HIDDeviceDescriptor] = [:]
    private var routes: [String: Route] = [:]
    private var refreshes: [String: (token: UUID, task: Task<Void, Never>)] = [:]
    private var polling: Task<Void, Never>?
    private var lastScanning: Bool?
    private var eventTask: Task<Void, Never>?
    private var eventContinuation: AsyncStream<HIDEvent>.Continuation?
    private var coordinator: SwitchCoordinator?
    private var callback: (@Sendable (EngineEvent) -> Void)?
    private var selectedKeyboards: Set<String> = []
    private var selectedMice: Set<String> = []
    private var follow = false
    private var manual = ManualSwitchGuard()
    private var manualRunGenerations: [Int: UInt64] = [:]
    private var manualGeneration: UInt64 = 0
    private var manualPreparations: [String: UInt64] = [:]
    private var manualSourceGenerations: Set<UInt64> = []
    private var deferredFollow: Task<Void, Never>?
    private var nextGeneration: UInt64 = 0
    private var started = false
    private var stopping = false
    private var relayConfiguration: (id: UUID, secret: Data?)?
    private var relay: LANRelay?
    private var notificationTasks: [String: (token: UUID, task: Task<Void, Never>)] = [:]
    private var notifiedReceivers: Set<String> = []
    private var notificationRetryAfter: [String: Int64] = [:]
    private var pairings: [String: ReceiverPairing] = [:]
    private var pairingTokens: [String: UUID] = [:]
    private var pairingTimeouts: [String: Task<Void, Never>] = [:]
    private let softwareID = 0x09
    private var noticeRun = 0
    private var requestStarts: [Int: Int64] = [:]
    private var pendingFollow: (run: Int, target: Int, devices: Set<String>)?

    public init(driver: (any HIDDriving)? = nil, log: FileLog) {
        let driver = driver ?? HIDMonitor(traceFrames: log.traceFrames)
        self.driver = driver; self.log = log; self.router = RequestRouter(driver: driver, log: log)
    }

    public func start(onEvent: @escaping @Sendable (EngineEvent) -> Void) async throws {
        guard !started else { return }
        started = true; stopping = false; callback = onEvent; lastScanning = nil
        coordinator = SwitchCoordinator(devices: self, log: { [log] in log.write($0) })
        log.write("EasySwitch macOS 0.1.9 | softwareID=09 | dynamic discovery | \(ProcessInfo.processInfo.operatingSystemVersionString)")
        log.writeCode("ES1|V0213|APP|START")
        let (stream, continuation) = AsyncStream<HIDEvent>.makeStream()
        eventContinuation = continuation
        eventTask = Task { [weak self] in
            for await event in stream {
                if Task.isCancelled { break }
                await self?.receive(event)
            }
        }
        do { try await driver.start { event in continuation.yield(event) } }
        catch {
            started = false; continuation.finish(); eventTask?.cancel(); eventTask = nil
            await driver.stop(); throw error
        }
        polling = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                do { try await Task.sleep(for: .milliseconds(100)) } catch { break }
            }
        }
    }

    public func stop() async {
        stopping = true
        polling?.cancel(); polling = nil
        notificationTasks.values.forEach { $0.task.cancel() }; notificationTasks.removeAll()
        for id in Array(pairings.keys) { await cancelPairing(id) }
        relay?.stop(); relay = nil; relayConfiguration = nil
        started = false
        notificationTasks.values.forEach { $0.task.cancel() }; notificationTasks.removeAll()
        notifiedReceivers.removeAll(); notificationRetryAfter.removeAll()
        polling?.cancel(); polling = nil
        cancelDeferred(stage: "CAN_STOP")
        for item in refreshes.values { item.task.cancel() }
        refreshes.removeAll()
        await coordinator?.stop(reason: "앱 중지")
        await driver.stop()
        eventContinuation?.finish(); eventContinuation = nil
        eventTask?.cancel(); eventTask = nil
        for endpoint in Set(endpoints.values.map(\.physicalID)) { await router.disconnect(endpoint) }
        endpoints.removeAll(); routes.removeAll(); publish()
        log.write("HID 감시 중지")
        log.writeCode("ES1|V0213|APP|STOP")
        await log.flush()
    }

    public func configure(keyboardID: String?, mouseID: String?, followEnabled: Bool) async {
        await configure(keyboardIDs: Set([keyboardID].compactMap { $0 }), mouseIDs: Set([mouseID].compactMap { $0 }), followEnabled: followEnabled)
    }

    public func configure(keyboardIDs: Set<String>, mouseIDs: Set<String>, followEnabled: Bool) async {
        guard selectedKeyboards != keyboardIDs || selectedMice != mouseIDs || follow != followEnabled else { return }
        cancelDeferred(stage: "CAN_CFG")
        manualGeneration &+= 1
        selectedKeyboards = keyboardIDs; selectedMice = mouseIDs; follow = followEnabled
        log.writeCode("ES1|V0213|CFG|F\(followEnabled ? 1 : 0)|K\(keyboardIDs.count)|M\(mouseIDs.count)")
        log.write("따라가기 설정: enabled=\(followEnabled) keyboards=\(keyboardIDs.sorted()) mice=\(mouseIDs.sorted())")
        await coordinator?.cancelAll(reason: "선택 또는 따라가기 설정 변경")
    }

    public func configureLAN(id: UUID, secret: Data?) {
        if let current = relayConfiguration, current.id == id, current.secret == secret { return }
        relayConfiguration = (id, secret)
        relay?.stop(); relay = nil
        guard let secret else { return }
        do {
            let relay = try LANRelay(id: id, secret: secret, log: { [log] in log.write($0) }, onArrival: { [weak self] channel in await self?.switchMice(channel) })
            self.relay = relay; relay.start()
        } catch { log.write("LAN 인증 설정 실패: \(error)") }
    }

    public func refresh(force: Bool = false) {
        guard started, !stopping else { return }
        if follow {
            for receiver in Set(endpoints.values.filter { $0.isReceiver && $0.isOpen }.map(\.physicalID)) {
                prepareReceiverNotifications(receiver)
            }
        }
        for id in routes.keys where refreshes[id] == nil && (force || Self.nowMS >= routes[id]!.nextRefreshAt) {
            let token = UUID()
            let task = Task { [weak self] in
                await self?.refreshRoute(id)
                await self?.finishedRefresh(id, token: token)
            }
            refreshes[id] = (token, task)
        }
        publishScanning()
    }

    private func publishScanning() {
        let scanning = !refreshes.isEmpty
        guard lastScanning != scanning else { return }
        lastScanning = scanning
        callback?(.scanning(scanning))
    }

    private func prepareReceiverNotifications(_ receiverID: String) {
        guard !notifiedReceivers.contains(receiverID), notificationTasks[receiverID] == nil,
              Self.nowMS >= (notificationRetryAfter[receiverID] ?? 0), outputEndpoint(receiverID) != nil else { return }
        let token = UUID()
        let task = Task { [weak self] in
            await self?.enableReceiverNotifications(receiverID)
            await self?.receiverNotificationsFinished(receiverID, token: token)
        }
        notificationTasks[receiverID] = (token, task)
    }
    private func receiverNotificationsFinished(_ id: String, token: UUID) {
        if notificationTasks[id]?.token == token { notificationTasks[id] = nil }
    }
    private func enableReceiverNotifications(_ id: String) async {
        guard started, !stopping, follow, let endpoint = outputEndpoint(id), endpoint.isReceiver else { return }
        do {
            let flags = try await router.request(deviceID: endpoint.id, responseDeviceID: id,
                frame: ReceiverPairing.register(0, parameters: [], read: true))
            guard started, !stopping, follow, !Task.isCancelled else { return }
            if flags.param(1) & 1 == 0 {
                let bytes = try ReceiverMessages.enableWirelessNotifications(registerReply: flags.bytes)
                let update = try HidppFrame(reportType: .short, deviceIndex: 0xFF, featureIndex: bytes[2], funcSwByte: bytes[3], parameters: Array(bytes[4...6]))
                _ = try await router.request(deviceID: endpoint.id, responseDeviceID: id, frame: update)
            }
            let verified = try await router.request(deviceID: endpoint.id, responseDeviceID: id,
                frame: ReceiverPairing.register(0, parameters: [], read: true))
            guard verified.param(1) & 1 != 0 else { throw EngineError.invalidResponse }
            guard started, !Task.isCancelled else { return }
            notifiedReceivers.insert(id)
            log.write("[\(id)] 수신기 무선 알림 확인: 다른 설정 비트 보존")
        } catch {
            notificationRetryAfter[id] = Self.nowMS + 3000
            if !Task.isCancelled { log.write("[\(id)] 수신기 무선 알림 설정 미확인; 재시도 예정: \(error)") }
        }
    }

    private func finishedRefresh(_ id: String, token: UUID) {
        guard refreshes[id]?.token == token else { return }
        refreshes[id] = nil
        if let route = routes[id], route.nextRefreshAt <= Self.nowMS {
            let locallyAttached = outputEndpoint(route.endpoint) != nil && route.localLink != false
            let preparing = locallyAttached && (manualPreparations[id] != nil || (Self.nowMS - route.firstSeenAt < 15_000 && !route.state.online))
            routes[id]?.nextRefreshAt = Self.nowMS + (preparing ? 100 : 3000)
        }
        publishScanning()
    }

    public func currentDevices() -> [DeviceState] {
        // An unresponsive probe slot is not a discovered or registered device.
        routes.values.filter { $0.state.featureIndex > 0 || $0.metadataRead }.map(\.state).sorted { $0.name < $1.name }
    }

    public func switchChannel(_ channel: Int, source: String = "B",
                              presentNotice: (@Sendable (EngineEvent) async -> Void)? = nil) async {
        guard !stopping, (1...3).contains(channel), let coordinator else { log.write("수동 전환 생략: 잘못된 채널 또는 엔진 중지"); return }
        cancelDeferred(stage: "CAN_MAN")
        manualGeneration &+= 1
        let generation = manualGeneration
        await coordinator.cancelAll(reason: "새 수동 요청")
        guard started, manualGeneration == generation else { log.write("수동 전환 취소: 엔진 중지 또는 더 최신 요청"); return }
        let devices = selectedKeyboards.union(selectedMice).compactMap { id -> (id: String, connection: UInt64)? in
            guard let route = routes[id], outputEndpoint(route.endpoint) != nil,
                  route.localLink != false, !route.departing else {
                log.write("[\(id)] 수동 전환 생략: 선택한 기기의 로컬 연결 없음")
                return nil
            }
            return (id, route.generation)
        }
        let run = beginRequest()
        manualRunGenerations[run] = generation
        activity(run: run, target: channel - 1, source: source, role: "D1", stage: "BEGIN")
        guard !devices.isEmpty else {
            callback?(.status("전환할 기기가 없음")); callback?(.notice(run: run, target: channel - 1, rows: [], connection: false))
            activity(run: run, target: channel - 1, source: source, role: "D1", stage: "SKIP_LINK")
            log.write("수동 전환 생략: 선택된 로컬 기기 없음"); return
        }
        manualSourceGenerations = Set(selectedKeyboards.compactMap { routes[$0]?.generation })
        manual.begin(generation: generation, target: channel - 1)
        let notice = EngineEvent.notice(run: run, target: channel - 1,
                                       rows: devices.compactMap { noticeRow($0.id, state: .switching) }, connection: false)
        if let presentNotice { await presentNotice(notice) } else { callback?(notice) }
        guard started, manualGeneration == generation else {
            manual.complete(generation: generation)
            log.write("수동 전환 취소: 안내 표시 중 요청 또는 연결 변경")
            return
        }
        callback?(.status("채널 \(channel) 전환 중"))
        await withTaskGroup(of: Void.self) { group in
            for device in devices {
                group.addTask {
                    await self.switchManualWhenReady(device.id, connection: device.connection, generation: generation,
                                                     target: channel - 1, run: run, source: source)
                }
            }
        }
        manual.complete(generation: generation)
        if manualGeneration == generation { callback?(.status("기기별 전환 결과를 로그에 기록했어")) }
    }

    public func cancelUnpresentedRequest(_ run: Int) async {
        guard manualRunGenerations[run] == manualGeneration else { return }
        manualGeneration &+= 1
        await coordinator?.cancelAll(reason: "화면 표시 확인 만료")
        log.write("수동 전환 취소: 초기 안내 표시를 확인하지 못함; 요청 \(run)")
    }

    public func switchMice(_ channel: Int, source: String = "L") async {
        guard started, !stopping, follow, (1...3).contains(channel), let coordinator else { log.write("LAN 마우스 전환 생략: 따라가기 꺼짐 또는 엔진/채널 없음"); return }
        cancelDeferred(stage: "CAN_LAN"); manualGeneration &+= 1
        let generation = manualGeneration
        await coordinator.cancelAll(reason: "인증된 LAN 키보드 도착")
        guard generation == manualGeneration else { return }
        let run = beginRequest()
        let mice = selectedMice.sorted()
        if mice.isEmpty { log.write("LAN 마우스 전환 생략: 선택 마우스 없음") }
        await withTaskGroup(of: Void.self) { group in
            for id in mice {
                guard let route = routes[id], route.localLink != false, !route.departing else { log.write("[\(id)] LAN 전환 생략: 로컬 연결 없음"); continue }
                group.addTask { await self.switchManualWhenReady(id, connection: route.generation, generation: generation, target: channel - 1, run: run, source: source) }
            }
        }
    }

    public func beginPairing(_ receiverID: String) async {
        guard started, !stopping, let endpoint = outputEndpoint(receiverID), endpoint.isReceiver,
              endpoint.productID == 0xC548 || endpoint.productID == 0xC52B else {
            log.write("수신기 페어링 생략: 지원 수신기 없음"); return
        }
        guard pairings[receiverID] == nil else { log.write("수신기 페어링 생략: 이미 진행 중"); return }
        let token = UUID(); pairingTokens[receiverID] = token
        var known = Set(routes.values.filter { $0.endpoint == receiverID && ($0.metadataRead || $0.wirelessProductID != nil) }.map { UInt8($0.index) })
        // Reserve the session before the first await, so repeated UI clicks cannot
        // start overlapping discovery commands on the same receiver.
        pairings[receiverID] = ReceiverPairing(receiverID: receiverID, bolt: endpoint.productID == 0xC548, knownSlots: known)
        if endpoint.productID == 0xC52B {
            do {
                for slot in UInt8(1)...UInt8(6) {
                    do {
                        _ = try await router.request(deviceID: endpoint.id, responseDeviceID: receiverID,
                            frame: ReceiverPairing.register(0xB5, parameters: [0x20 + slot - 1], read: true))
                        known.insert(slot)
                    } catch EngineError.protocolError(let bytes) {
                        guard let error = HidppParser.parseError(bytes), error.protocolVersion == 1,
                              [8, 9].contains(error.errorCode) else { throw EngineError.protocolError(bytes) }
                    }
                    guard pairingTokens[receiverID] == token else { return }
                }
            } catch {
                log.write("수신기 페어링 중단: 기존 슬롯 확인 실패; 등록 목록을 추측하지 않음: \(error)")
                if pairingTokens[receiverID] == token { await cancelPairing(receiverID) }; return
            }
        }
        let pairing = ReceiverPairing(receiverID: receiverID, bolt: endpoint.productID == 0xC548, knownSlots: known)
        pairings[receiverID] = pairing
        do {
            // Preserve existing flag bytes and add only wireless notifications.
            let flags = try await router.request(deviceID: endpoint.id, responseDeviceID: receiverID,
                frame: ReceiverPairing.register(0, parameters: [], read: true))
            guard pairingTokens[receiverID] == token else { return }
            let bytes = try ReceiverMessages.enableWirelessNotifications(registerReply: flags.bytes)
            let update = try HidppFrame(reportType: .short, deviceIndex: 0xFF, featureIndex: bytes[2], funcSwByte: bytes[3], parameters: Array(bytes[4...6]))
            _ = try await router.request(deviceID: endpoint.id, responseDeviceID: receiverID, frame: update)
            let verified = try await router.request(deviceID: endpoint.id, responseDeviceID: receiverID,
                frame: ReceiverPairing.register(0, parameters: [], read: true))
            guard verified.param(1) & 1 != 0 else { throw EngineError.invalidResponse }
            guard pairingTokens[receiverID] == token else { return }
            _ = try await router.request(deviceID: endpoint.id, responseDeviceID: receiverID, frame: pairing.search())
            guard pairingTokens[receiverID] == token, let current = pairings[receiverID] else { return }
            callback?(.pairing(current.progress))
            pairingTimeouts[receiverID] = Task { [weak self] in
                do { try await Task.sleep(for: .seconds(90)) } catch { return }
                await self?.expirePairing(receiverID, token: token)
            }
        } catch { log.write("수신기 페어링 시작 실패: \(error)"); if pairingTokens[receiverID] == token { await cancelPairing(receiverID) } }
    }

    public func confirmPairing(_ receiverID: String) async {
        guard var pairing = pairings[receiverID], let token = pairingTokens[receiverID], let endpoint = outputEndpoint(receiverID) else { return }
        do {
            let command = try pairing.confirm(); pairings[receiverID] = pairing
            _ = try await router.request(deviceID: endpoint.id, responseDeviceID: receiverID, frame: command)
            if pairingTokens[receiverID] == token, let latest = pairings[receiverID] { callback?(.pairing(latest.progress)) }
        } catch { log.write("수신기 페어링 확인 실패: \(error)"); if pairingTokens[receiverID] == token { await cancelPairing(receiverID) } }
    }

    private func expirePairing(_ receiverID: String, token: UUID) async {
        guard pairingTokens[receiverID] == token else { return }
        await cancelPairing(receiverID)
    }
    public func cancelPairing(_ receiverID: String) async {
        pairingTokens[receiverID] = nil
        pairingTimeouts.removeValue(forKey: receiverID)?.cancel()
        guard let pairing = pairings.removeValue(forKey: receiverID) else { return }
        if let endpoint = outputEndpoint(receiverID) {
            do { _ = try await router.request(deviceID: endpoint.id, responseDeviceID: receiverID, frame: pairing.cancel()) }
            catch { log.write("수신기 페어링 취소 미확인: \(error)") }
        }
        callback?(.pairing(.init(receiverID: receiverID, stage: "closed")))
    }

    public func switchSnapshot(_ id: String) -> SwitchSnapshot? {
        guard let route = routes[id] else { return nil }
        return SwitchSnapshot(generation: route.generation, online: route.state.online,
                              localLink: route.localLink, hostCount: route.state.hostCount, currentHost: route.state.currentHost)
    }

    public func writeHost(_ id: String, target: Int) async throws {
        guard let route = routes[id], route.state.online, route.localLink != false,
              target < route.state.hostCount, let endpoint = outputEndpoint(route.endpoint) else { throw EngineError.disconnected }
        let frame = try ChangeHostFeature.setCurrentHost(featureIndex: route.state.featureIndex, hostIndex: target,
                                                        deviceIndex: route.index, softwareID: softwareID)
        try await router.send(deviceID: endpoint.id, responseDeviceID: route.endpoint, frame: output(frame, endpoint: endpoint)) { [weak self] in
            guard let self else { throw EngineError.disconnected }
            try await self.validateWrite(id, generation: route.generation, feature: route.state.featureIndex)
        }
    }

    private func validateWrite(_ id: String, generation: UInt64, feature: Int) throws {
        guard let route = routes[id], route.generation == generation,
              route.state.featureIndex == feature, feature != 0, route.localLink != false,
              route.state.online, outputEndpoint(route.endpoint) != nil else {
            log.write("[\(id)] 전환 쓰기 취소: 대기 중 기기 연결 또는 feature 변경")
            throw EngineError.disconnected
        }
    }

    public func readHost(_ id: String) async throws -> HostInfo {
        guard let route = routes[id], route.state.featureIndex != 0 else { throw EngineError.unsupported }
        let frame = try ChangeHostFeature.getHostInfo(featureIndex: route.state.featureIndex, deviceIndex: route.index, softwareID: softwareID)
        let response = try await query(id, frame)
        let host = try ChangeHostFeature.parseHostInfo(response)
        guard var latest = routes[id], latest.generation == route.generation, latest.revision == route.revision,
              (!latest.departing || Self.nowMS - latest.departedAt > 1500) else { throw EngineError.disconnected }
        let returned = latest.localLink == false
        if returned {
            nextGeneration &+= 1; latest.generation = nextGeneration
            latest.early = EarlySignalGate<HidppFrame>()
        }
        latest.localLink = true; latest.observedLocally = true; latest.departing = false
        latest.state.online = true; latest.state.currentHost = host.currentHost; latest.state.hostCount = host.hostCount
        routes[id] = latest
        if !route.state.online {
            log.write("[\(id)] 기기 연결 확인: name=\(latest.state.name) channel=\(host.currentHost + 1) evidence=host_info_response")
        }
        if !route.state.online, selectedKeyboards.contains(id) {
            callback?(.keyboardArrival(id: id, channel: host.currentHost + 1))
            if follow { relay?.keyboardArrived(channel: host.currentHost + 1) }
        }
        if !route.state.online, latest.metadataRead, let row = noticeRow(id, state: .connected) {
            callback?(.notice(run: 0, target: 0, rows: [row], connection: true))
        }
        if returned, selectedKeyboards.contains(id) {
            cancelDeferred(stage: "CAN_RET")
            manualGeneration &+= 1
            await coordinator?.cancelAll(reason: "키보드 로컬 연결 재확인")
        }
        publish()
        return host
    }

    private func query(_ id: String, _ frame: HidppFrame) async throws -> HidppFrame {
        guard let route = routes[id], let endpoint = outputEndpoint(route.endpoint) else { throw EngineError.disconnected }
        return try await router.request(deviceID: endpoint.id, responseDeviceID: route.endpoint, frame: output(frame, endpoint: endpoint))
    }

    private func outputEndpoint(_ physicalID: String) -> HIDDeviceDescriptor? {
        endpoints.values.filter { $0.physicalID == physicalID && $0.isOpen && $0.isHidppCandidate }
            .sorted {
                let lhsLong = $0.supportedReportIDs.contains(0x11) || ($0.supportedReportIDs.isEmpty && $0.maxOutputReportSize >= 20)
                let rhsLong = $1.supportedReportIDs.contains(0x11) || ($1.supportedReportIDs.isEmpty && $1.maxOutputReportSize >= 20)
                return lhsLong == rhsLong ? $0.id < $1.id : lhsLong
            }.first
    }

    private func output(_ frame: HidppFrame, endpoint: HIDDeviceDescriptor) -> HidppFrame {
        endpoint.supportedReportIDs.contains(0x11) || (!endpoint.supportedReportIDs.contains(0x10) && endpoint.maxOutputReportSize >= 20)
            ? frame.toLong() : frame
    }

    private func refreshRoute(_ id: String) async {
        guard let initial = routes[id] else { return }
        do {
            if initial.state.featureIndex == 0 {
                let response = try await query(id, RootFeature.getFeature(0x1814, deviceIndex: initial.index, softwareID: softwareID))
                guard var route = routes[id], route.generation == initial.generation else { return }
                let index = RootFeature.parseLookup(response).featureIndex
                guard index != 0 else {
                    routes[id]?.nextRefreshAt = Self.nowMS + 30_000
                    log.write("[\(id)] 0x1814 미지원; 전환 명령 없음"); return
                }
                route.state.featureIndex = index
                let held = route.early.open(currentGeneration: route.generation, nowMilliseconds: Self.nowMS)
                routes[id] = route
                if held.discarded > 0 { log.write("[\(id)] 오래된 초기 신호 \(held.discarded)개 폐기") }
                for frame in held.frames { await handleDeparture(id, frame: frame) }
            }
            _ = try await readHost(id)
            if routes[id]?.metadataRead == false { try await readMetadata(id) }
        } catch is CancellationError { }
        catch EngineError.protocolError(let raw) {
            if let error = HidppParser.parseError(raw), error.protocolVersion == 1, error.errorCode == 9 {
                routes[id]?.nextRefreshAt = Self.nowMS + 30_000
                log.write("[\(id)] 수신기가 없는 슬롯으로 응답; 30초 뒤 재조회")
            } else {
                log.write("[\(id)] 조회 프로토콜 오류: version=\(HidppParser.parseError(raw)?.protocolVersion ?? 0) code=\(HidppParser.parseError(raw).map { Int($0.errorCode) } ?? -1)")
                log.writeTrace("[\(id)] 조회 오류 frame=\(RequestRouter.hex(raw))")
            }
        }
        catch {
            log.write("[\(id)] 조회 미완료: \(error); 응답 없음은 빈 슬롯 또는 로컬 이탈의 증거가 아님")
        }
    }

    private func readMetadata(_ id: String) async throws {
        guard let route = routes[id] else { throw EngineError.disconnected }
        let response = try await query(id, RootFeature.getFeature(0x0005, deviceIndex: route.index, softwareID: softwareID))
        let index = RootFeature.parseLookup(response).featureIndex
        guard index != 0 else { log.write("[\(id)] 이름 기능 미지원"); return }
        let kind = try await query(id, HidppFrame.build(featureIndex: index, functionID: 2, deviceIndex: route.index, softwareID: softwareID))
        let count = try await query(id, DeviceNameFeature.getCount(featureIndex: index, deviceIndex: route.index, softwareID: softwareID))
        let length = min(128, Int(count.param(0)))
        var bytes: [UInt8] = []
        while bytes.count < length {
            let chunk = try await query(id, DeviceNameFeature.getName(featureIndex: index, characterIndex: bytes.count,
                                                                     deviceIndex: route.index, softwareID: softwareID))
            bytes.append(contentsOf: chunk.parameters.prefix(length - bytes.count))
        }
        guard var latest = routes[id], latest.generation == route.generation, latest.revision == route.revision else { return }
        let name = String(decoding: bytes.prefix(while: { $0 != 0 }), as: UTF8.self)
        if !name.isEmpty { latest.state.name = name }
        latest.state.kind = [0, 2].contains(kind.param(0)) ? "키보드" : ([3, 4].contains(kind.param(0)) ? "마우스" : "기기")
        latest.metadataRead = true; routes[id] = latest
        log.write("[\(id)] 이름=\(latest.state.name) 종류=\(latest.state.kind) 채널=\(latest.state.currentHost.map { String($0 + 1) } ?? "unknown")")
        publish()
        if let row = noticeRow(id, state: .connected) { callback?(.notice(run: 0, target: 0, rows: [row], connection: true)) }
    }

    private func receive(_ event: HIDEvent) async {
        guard started else { return }
        switch event {
        case .diagnostic(let message): log.write(message)
        case .added(let descriptor):
            let hadPhysicalEndpoint = outputEndpoint(descriptor.physicalID) != nil
            endpoints[descriptor.id] = descriptor
            log.writeTrace("HID 열거: \(descriptor)")
            guard descriptor.isHidppCandidate else { return }
            if !hadPhysicalEndpoint || !descriptor.isOpen {
                log.write("HID 통로: name=\(descriptor.name) connection=\(descriptor.transport) open=\(descriptor.isOpen) result=\(descriptor.openResult.map(String.init) ?? "unknown")")
            }
            guard descriptor.isOpen else {
                if let result = descriptor.openResult, HIDTransportError.isPermissionDenied(result) {
                    callback?(.inputMonitoringRequired)
                    log.write("기기 열기 거부: 앱의 입력 모니터링 권한을 사용자가 확인해야 함")
                }
                callback?(.status("기기 접근 상태를 연결 로그에 기록했어")); return
            }
            let indices = descriptor.isReceiver ? Array(1...6) : [0xFF]
            for index in indices {
                let id = "\(descriptor.physicalID)#\(index)"
                if hadPhysicalEndpoint, routes[id] != nil { continue }
                let previous = routes[id]
                refreshes[id]?.task.cancel(); refreshes[id] = nil
                nextGeneration &+= 1
                let state = DeviceState(id: id, name: descriptor.isReceiver ? "\(descriptor.name) · 기기 \(index)" : descriptor.name,
                                        connection: descriptor.isReceiver ? (descriptor.productID == 0xC548 ? "Bolt" : "수신기") : descriptor.transport,
                                        kind: "기기", online: false, currentHost: nil, hostCount: 0, featureIndex: 0)
                routes[id] = Route(endpoint: descriptor.physicalID, index: index, generation: nextGeneration,
                                   observedLocally: previous?.observedLocally ?? false, state: state)
                if let previous {
                    routes[id]?.state.name = previous.state.name; routes[id]?.state.kind = previous.state.kind
                    if !descriptor.isReceiver, descriptor.transport.localizedCaseInsensitiveContains("bluetooth"),
                       let serial = descriptor.serialNumber, !serial.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        routes[id]?.metadataRead = previous.metadataRead
                        if previous.metadataRead { log.write("[\(id)] 같은 블루투스 기기의 HID 통로 재열거: 확인한 이름·종류 유지; 연결은 새 채널 응답으로 확인 예정") }
                    }
                }
                if selectedKeyboards.contains(id) {
                    cancelDeferred(stage: "CAN_RET")
                    manualGeneration &+= 1
                    await coordinator?.cancelAll(reason: "키보드 HID 통로 재열거")
                }
            }
            publish(); refresh()
        case .removed(let endpointID):
            guard let removed = endpoints.removeValue(forKey: endpointID) else { return }
            guard outputEndpoint(removed.physicalID) == nil else { return }
            if removed.isReceiver { await cancelPairing(removed.physicalID) }
            notificationTasks.removeValue(forKey: removed.physicalID)?.task.cancel()
            notifiedReceivers.remove(removed.physicalID); notificationRetryAfter[removed.physicalID] = nil
            await router.disconnect(removed.physicalID)
            for id in routes.keys where routes[id]?.endpoint == removed.physicalID {
                routes[id]?.revision &+= 1; routes[id]?.localLink = false; routes[id]?.state.online = false
                routes[id]?.early = EarlySignalGate<HidppFrame>()
                refreshes[id]?.task.cancel(); refreshes[id] = nil
            }
            log.write("로컬 HID 통로 종료: \(endpointID); 목적지 도착은 미확인")
            publish()
        case .report(let deviceID, let reportID, let bytes):
            // A macOS IOHIDDevice can expose generic input and the vendor collection together.
            // Never copy ordinary keyboard/mouse reports into diagnostic files.
            guard reportID == 0x10 || reportID == 0x11 else { return }
            log.writeTrace("RX \(deviceID) reportID=\(reportID) raw=\(RequestRouter.hex(bytes))")
            do {
                let raw = try HIDReportNormalizer.normalize(reportID: reportID, bytes: bytes)
                guard let endpoint = endpoints[deviceID] else { return }
                await router.receive(deviceID: endpoint.physicalID, bytes: raw)
                guard raw.count >= 7 else { return }
                let id = "\(endpoint.physicalID)#\(raw[1])"
                if var pairing = pairings[endpoint.physicalID], let frame = HidppParser.parse(raw) {
                    let before = pairing.progress
                    pairing.accept(frame); pairings[endpoint.physicalID] = pairing
                    if before != pairing.progress { callback?(.pairing(pairing.progress)) }
                    if pairing.progress.stage == "complete" || pairing.progress.stage == "failed" {
                        pairings[endpoint.physicalID] = nil; pairingTokens[endpoint.physicalID] = nil
                        pairingTimeouts.removeValue(forKey: endpoint.physicalID)?.cancel()
                        refresh(force: true)
                    }
                }
                guard var route = routes[id] else { return }
                if let notice = ReceiverMessages.parseDeviceConnection(raw), endpoints[deviceID]?.isReceiver == true {
                    let linkChanged = route.localLink != notice.linkEstablishedAssumed
                    let pairingChanged = route.wirelessProductID.map { $0 != notice.wirelessProductID } ?? false
                    let arrived = notice.linkEstablishedAssumed && (route.localLink != true || route.departing || pairingChanged)
                    route.wirelessProductID = notice.wirelessProductID
                    route.revision &+= 1
                    route.localLink = notice.linkEstablishedAssumed
                    if pairingChanged {
                        route.metadataRead = false; route.state.featureIndex = 0
                        route.state.hostCount = 0; route.state.currentHost = nil; route.state.online = false
                        route.state.name = "\(endpoint.name) · 기기 \(route.index)"; route.state.kind = "기기"
                        selectedKeyboards.remove(id)
                        selectedMice.remove(id)
                        cancelDeferred(stage: "CAN_CFG")
                        manualGeneration &+= 1
                        await coordinator?.cancelAll(reason: "수신기 등록 기기 변경")
                        callback?(.selectionInvalidated(id))
                        log.write("[\(id)] 등록 기기 변경: 선택과 이전 feature/이름/채널 캐시 폐기")
                    }
                    if arrived {
                        nextGeneration &+= 1; route.generation = nextGeneration; route.departing = false
                        route.state.featureIndex = 0; route.nextRefreshAt = 0
                        route.early = EarlySignalGate<HidppFrame>()
                        if selectedKeyboards.contains(id) { cancelDeferred(stage: "CAN_RET"); manualGeneration &+= 1; await coordinator?.cancelAll(reason: "키보드 복귀") }
                    } else if !notice.linkEstablishedAssumed { route.state.online = false }
                    routes[id] = route
                    if arrived || pairingChanged { refreshes[id]?.task.cancel(); refreshes[id] = nil }
                    if arrived || pairingChanged || linkChanged {
                        log.write("[\(id)] 수신기 기기 \(notice.linkEstablishedAssumed ? "연결" : "연결 해제"); 목적지 도착은 별도 확인 필요")
                    }
                    log.writeTrace("[\(id)] 수신기 링크 알림 flags=\(notice.flags) local=\(notice.linkEstablishedAssumed)")
                    publish(); refresh(force: true)
                } else if let frame = HidppParser.parse(raw), frame.funcSwByte == 0 {
                    if route.state.featureIndex == 0 {
                        let evicted = route.early.hold(frame, generation: route.generation, nowMilliseconds: Self.nowMS)
                        routes[id] = route
                        if evicted { log.write("[\(id)] 초기 신호 보관 한도 초과: 이전 신호 한 개 폐기") }
                        log.writeTrace("[\(id)] 초기 자발 신호 보관")
                    } else { await handleDeparture(id, frame: frame) }
                } else if let frame = HidppParser.parse(raw), frame.softwareID != softwareID {
                    log.writeTrace("다른 앱 응답 softwareID=\(frame.softwareID)")
                }
            } catch { log.write("리포트 정규화 생략: \(error)") }
        }
    }

    private func handleDeparture(_ id: String, frame: HidppFrame) async {
        guard !stopping else { log.write("따라가기 생략: 엔진 중지 중"); return }
        guard var route = routes[id] else { return }
        guard let target = ChangeHostFeature.parseHostChangeEvent(frame, featureIndex: route.state.featureIndex) else {
            log.writeTrace("[\(id)] 전환 이외 자발 신호: feature/목적지 범위 불일치"); return
        }
        route.departing = true; route.departedAt = Self.nowMS; route.revision &+= 1; routes[id] = route
        log.write("[\(id)] 출발 신호: 채널 \(target + 1); 목적지 도착은 미확인")
        let run = beginRequest()
        activity(run: run, target: target, source: "F", role: "K1", stage: "KEY")
        guard selectedKeyboards.contains(id) else { activity(run: run, target: target, source: "F", role: "D1", stage: "SKIP_KEY"); log.write("따라가기 생략: 선택한 키보드 아님"); return }
        guard follow else { activity(run: run, target: target, source: "F", role: "K1", stage: "SKIP_OFF"); log.write("따라가기 생략: 설정 꺼짐"); return }
        guard !manualSourceGenerations.contains(route.generation) || !manual.matches(generation: manualGeneration, target: target) else {
            activity(run: run, target: target, source: "F", role: "K1", stage: "SKIP_MAN"); log.write("따라가기 생략: 수동 요청 에코"); return
        }
        let mice = selectedMice.sorted().filter { mouse in
            guard let mouseRoute = routes[mouse] else {
                log.write("[\(mouse)] 따라가기 생략: 선택한 마우스 경로 없음"); return false
            }
            let readiness = FollowPreparation.readiness(exists: true, transport: outputEndpoint(mouseRoute.endpoint) != nil,
                localLink: mouseRoute.localLink, online: mouseRoute.state.online,
                hosts: mouseRoute.state.hostCount, observedLocally: mouseRoute.observedLocally)
            if readiness == .exclude { log.write("[\(mouse)] 따라가기 생략: 로컬 연결을 확인하지 못함"); return false }
            return true
        }
        guard !mice.isEmpty else {
            activity(run: run, target: target, source: "F", role: "M1", stage: "SKIP_SEL")
            log.write("따라가기 생략: 전환할 선택 마우스 없음"); return
        }
        callback?(.keyboardDeparture(target: target))
        callback?(.notice(run: run, target: target,
            rows: [noticeRow(id, state: .complete)].compactMap { $0 } + mice.compactMap { noticeRow($0, state: .switching) }, connection: false))
        cancelDeferred(stage: "CAN_NEW")
        manualGeneration &+= 1
        let generation = manualGeneration
        await coordinator?.cancelAll(reason: "새 키보드 목적지")
        guard started, manualGeneration == generation else { log.write("따라가기 취소: 대기 중 새 요청"); return }
        pendingFollow = (run, target, Set(mice))
        deferredFollow = Task { [weak self] in
            await withTaskGroup(of: Void.self) { group in
                for mouse in mice {
                    group.addTask { [weak self] in
                        let started = Self.nowMS
                        while !Task.isCancelled {
                            guard let self else { return }
                            if await self.followMouseIfReady(mouse, target: target, run: run) { return }
                            if FollowPreparation.expired(started: started, now: Self.nowMS) {
                                await self.followExpired(mouse, run: run, target: target); return
                            }
                            do { try await Task.sleep(for: .milliseconds(100)) } catch { return }
                        }
                    }
                }
            }
        }
    }

    private func followMouseIfReady(_ id: String, target: Int, run: Int) async -> Bool {
        guard follow, selectedMice.contains(id), pendingFollow?.run == run, let route = routes[id] else { return true }
        guard route.state.online, route.localLink != false, route.state.hostCount > 0 else { return false }
        await switchOne(id, target: target, run: run, source: "F", reason: "키보드 출발")
        finishPendingMouse(id, run: run)
        return true
    }

    private func finishPendingMouse(_ id: String, run: Int) {
        guard pendingFollow?.run == run else { return }
        pendingFollow?.devices.remove(id)
        if pendingFollow?.devices.isEmpty == true { pendingFollow = nil }
    }

    private func deviceRole(_ id: String) -> String {
        if let index = selectedKeyboards.sorted().firstIndex(of: id) { return "K\(index + 1)" }
        if let index = selectedMice.sorted().firstIndex(of: id) { return "M\(index + 1)" }
        return "D1"
    }

    private func noticeRow(_ id: String, state: NoticeState) -> NoticeRow? {
        guard let device = routes[id]?.state else { return nil }
        return NoticeRow(id: id, name: device.name, kind: device.kind, state: state)
    }

    private func switchManualWhenReady(_ id: String, connection: UInt64, generation: UInt64,
                                       target: Int, run: Int, source: String) async {
        let startedAt = Self.nowMS
        let role = deviceRole(id)
        var waiting = false
        defer { if manualPreparations[id] == generation { manualPreparations[id] = nil } }
        while true {
            guard !Task.isCancelled, started, manualGeneration == generation,
                  selectedKeyboards.contains(id) || selectedMice.contains(id),
                  let route = routes[id], route.generation == connection,
                  outputEndpoint(route.endpoint) != nil, route.localLink != false, !route.departing else {
                activity(run: run, target: target, source: source, role: role, stage: "CAN_PREP")
                log.write("[\(id)] 수동 전환 대기 취소: 요청·선택·로컬 연결 변경")
                if let row = noticeRow(id, state: .unconfirmed) { callback?(.notice(run: run, target: target, rows: [row], connection: false)) }
                return
            }
            if route.state.online, route.state.featureIndex > 0, route.state.hostCount > 0 {
                await switchOne(id, target: target, run: run, source: source, reason: "수동")
                return
            }
            if !waiting {
                waiting = true
                manualPreparations[id] = generation
                activity(run: run, target: target, source: source, role: role, stage: "WAIT")
                log.write("[\(id)] 수동 전환 대기: 현재 로컬 연결의 채널 조회 완료 후 전송; 최대 10초")
                routes[id]?.nextRefreshAt = 0
                refresh()
            }
            if FollowPreparation.expired(started: startedAt, now: Self.nowMS) {
                activity(run: run, target: target, source: source, role: role, stage: "CAN_EXP")
                log.write("[\(id)] 수동 전환 대기 만료: 10초 동안 채널 조회 미완료")
                if let row = noticeRow(id, state: .unconfirmed) { callback?(.notice(run: run, target: target, rows: [row], connection: false)) }
                return
            }
            do { try await Task.sleep(for: .milliseconds(100)) }
            catch {
                activity(run: run, target: target, source: source, role: role, stage: "CAN_PREP")
                log.write("[\(id)] 수동 전환 대기 취소: 작업 취소")
                return
            }
        }
    }

    private func switchOne(_ id: String, target: Int, run: Int, source: String, reason: String) async {
        guard let coordinator, let device = routes[id]?.state else { log.write("전환 생략: 기기 경로 사라짐"); return }
        let role = deviceRole(id)
        let requestStart = requestStarts[run] ?? Self.nowMS
        _ = await coordinator.switchHost(id, target: target, reason: reason) { [weak self, log] progress in
            log.writeCode(SwitchCode.format(request: run, source: source, target: target, role: role, progress: progress,
                                            requestElapsedMS: Self.nowMS - requestStart))
            let state: NoticeState
            switch progress.outcome {
            case .alreadyCurrent, .targetReported: state = .complete
            case .failed, .stillOnSource: state = .failed
            case .cancelled, .unverified, .localLinkClosed: state = .unconfirmed
            case nil: state = .switching
            }
            Task { await self?.emitProgress(run: run, target: target, device: device, state: state) }
        }
    }

    private func emitProgress(run: Int, target: Int, device: DeviceState, state: NoticeState) {
        callback?(.notice(run: run, target: target, rows: [NoticeRow(id: device.id, name: device.name, kind: device.kind, state: state)], connection: false))
    }

    private func beginRequest() -> Int {
        noticeRun += 1; requestStarts[noticeRun] = Self.nowMS
        if requestStarts.count > 200 { requestStarts[noticeRun - 200] = nil; manualRunGenerations[noticeRun - 200] = nil }
        return noticeRun
    }
    private func activity(run: Int, target: Int, source: String, role: String, stage: String) {
        log.writeCode("ES1|V0213|R\(String(format: "%04d", run))|\(source)\(target + 1)|\(role)|\(stage)|E\(Self.nowMS - (requestStarts[run] ?? Self.nowMS))")
    }
    private func cancelDeferred(stage: String) {
        if let pendingFollow { activity(run: pendingFollow.run, target: pendingFollow.target, source: "F", role: "M1", stage: stage) }
        pendingFollow = nil; deferredFollow?.cancel(); deferredFollow = nil
    }
    private func followExpired(_ id: String, run: Int, target: Int) {
        guard pendingFollow?.run == run else { return }
        activity(run: run, target: target, source: "F", role: "M1", stage: "CAN_EXP")
        if let row = noticeRow(id, state: .failed) { callback?(.notice(run: run, target: target, rows: [row], connection: false)) }
        finishPendingMouse(id, run: run)
        log.write("따라가기 대기 만료: 10초 동안 마우스 준비 안 됨")
    }

    private func publish() {
        callback?(.devices(currentDevices()))
        let receivers = Dictionary(grouping: endpoints.values.filter { $0.isReceiver && $0.isOpen }, by: \.physicalID)
            .compactMap { id, group -> ReceiverState? in
                guard let item = group.first, item.productID == 0xC548 || item.productID == 0xC52B else { return nil }
                return ReceiverState(id: id, name: item.name, bolt: item.productID == 0xC548)
            }.sorted { $0.id < $1.id }
        callback?(.receivers(receivers))
    }
    private static var nowMS: Int64 { Int64(ProcessInfo.processInfo.systemUptime * 1000) }
}
