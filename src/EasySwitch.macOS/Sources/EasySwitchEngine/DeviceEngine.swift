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

public enum EngineEvent: Sendable {
    case devices([DeviceState]), scanning(Bool), status(String), selectionInvalidated(String), inputMonitoringRequired
    case notice(run: Int, target: Int, rows: [NoticeRow], connection: Bool)
    case keyboardDeparture(target: Int)
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
    private var eventTask: Task<Void, Never>?
    private var eventContinuation: AsyncStream<HIDEvent>.Continuation?
    private var coordinator: SwitchCoordinator?
    private var callback: (@Sendable (EngineEvent) -> Void)?
    private var selectedKeyboard: String?
    private var selectedMouse: String?
    private var follow = false
    private var manual = ManualSwitchGuard()
    private var manualGeneration: UInt64 = 0
    private var manualPreparations: [String: UInt64] = [:]
    private var manualSourceGeneration: UInt64?
    private var deferredFollow: Task<Void, Never>?
    private var nextGeneration: UInt64 = 0
    private var started = false
    private let softwareID = 0x09
    private var noticeRun = 0
    private var requestStarts: [Int: Int64] = [:]
    private var pendingFollow: (run: Int, target: Int)?

    public init(driver: any HIDDriving = HIDMonitor(), log: FileLog) {
        self.driver = driver; self.log = log; self.router = RequestRouter(driver: driver, log: log)
    }

    public func start(onEvent: @escaping @Sendable (EngineEvent) -> Void) async throws {
        guard !started else { return }
        started = true; callback = onEvent
        coordinator = SwitchCoordinator(devices: self, log: { [log] in log.write($0) })
        log.write("EasySwitch macOS 0.1.4 | softwareID=09 | read-only discovery | \(ProcessInfo.processInfo.operatingSystemVersionString)")
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
        started = false
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
        guard selectedKeyboard != keyboardID || selectedMouse != mouseID || follow != followEnabled else { return }
        cancelDeferred(stage: "CAN_CFG")
        manualGeneration &+= 1
        // Commit in this actor turn. An earlier configure may resume after a newer
        // selection while cancellation crosses to the coordinator actor.
        selectedKeyboard = keyboardID; selectedMouse = mouseID; follow = followEnabled
        log.writeCode("ES1|V0213|CFG|F\(followEnabled ? 1 : 0)|K\(keyboardID == nil ? 0 : 1)|M\(mouseID == nil ? 0 : 1)")
        log.write("따라가기 설정: enabled=\(followEnabled) keyboard=\(keyboardID ?? "none") mouse=\(mouseID ?? "none")")
        await coordinator?.cancelAll(reason: "선택 또는 따라가기 설정 변경")
    }

    public func refresh(force: Bool = false) {
        guard started else { return }
        for id in routes.keys where refreshes[id] == nil && (force || Self.nowMS >= routes[id]!.nextRefreshAt) {
            let token = UUID()
            let task = Task { [weak self] in
                await self?.refreshRoute(id)
                await self?.finishedRefresh(id, token: token)
            }
            refreshes[id] = (token, task)
        }
        callback?(.scanning(!refreshes.isEmpty))
    }

    private func finishedRefresh(_ id: String, token: UUID) {
        guard refreshes[id]?.token == token else { return }
        refreshes[id] = nil
        if let route = routes[id], route.nextRefreshAt <= Self.nowMS {
            let locallyAttached = outputEndpoint(route.endpoint) != nil && route.localLink != false
            let preparing = locallyAttached && (manualPreparations[id] != nil || (Self.nowMS - route.firstSeenAt < 15_000 && !route.state.online))
            routes[id]?.nextRefreshAt = Self.nowMS + (preparing ? 100 : 3000)
        }
        callback?(.scanning(!refreshes.isEmpty))
    }

    public func currentDevices() -> [DeviceState] {
        // An unresponsive probe slot is not a discovered or registered device.
        routes.values.filter { $0.state.featureIndex > 0 || $0.metadataRead }.map(\.state).sorted { $0.name < $1.name }
    }

    public func switchChannel(_ channel: Int, source: String = "B",
                              presentNotice: (@Sendable (EngineEvent) async -> Void)? = nil) async {
        guard (1...3).contains(channel), let coordinator else { log.write("수동 전환 생략: 잘못된 채널 또는 엔진 중지"); return }
        cancelDeferred(stage: "CAN_MAN")
        manualGeneration &+= 1
        let generation = manualGeneration
        await coordinator.cancelAll(reason: "새 수동 요청")
        guard started, manualGeneration == generation else { log.write("수동 전환 취소: 엔진 중지 또는 더 최신 요청"); return }
        let devices = Set([selectedKeyboard, selectedMouse].compactMap { $0 }).compactMap { id -> (id: String, connection: UInt64)? in
            guard let route = routes[id], outputEndpoint(route.endpoint) != nil,
                  route.localLink != false, !route.departing else {
                log.write("[\(id)] 수동 전환 생략: 선택한 기기의 로컬 연결 없음")
                return nil
            }
            return (id, route.generation)
        }
        let run = beginRequest()
        activity(run: run, target: channel - 1, source: source, role: "D1", stage: "BEGIN")
        guard !devices.isEmpty else {
            callback?(.status("전환할 기기가 없음")); callback?(.notice(run: run, target: channel - 1, rows: [], connection: false))
            activity(run: run, target: channel - 1, source: source, role: "D1", stage: "SKIP_LINK")
            log.write("수동 전환 생략: 선택된 로컬 기기 없음"); return
        }
        manualSourceGeneration = selectedKeyboard.flatMap { routes[$0]?.generation }
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
        if !route.state.online, latest.metadataRead, let row = noticeRow(id, state: .connected) {
            callback?(.notice(run: 0, target: 0, rows: [row], connection: true))
        }
        if returned, id == selectedKeyboard {
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
            } else { log.write("[\(id)] 조회 프로토콜 오류: \(RequestRouter.hex(raw))") }
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
            log.write("HID 열거: \(descriptor)")
            guard descriptor.isHidppCandidate else { return }
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
                        if previous.metadataRead { log.write("[\(id)] 같은 블루투스 기기 재연결: 확인한 이름·종류 유지; 새 채널 응답으로 연결 확인") }
                    }
                }
                if id == selectedKeyboard {
                    cancelDeferred(stage: "CAN_RET")
                    manualGeneration &+= 1
                    await coordinator?.cancelAll(reason: "키보드 HID 재도착")
                }
            }
            publish(); refresh()
        case .removed(let endpointID):
            guard let removed = endpoints.removeValue(forKey: endpointID) else { return }
            guard outputEndpoint(removed.physicalID) == nil else { return }
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
            log.write("RX \(deviceID) reportID=\(reportID) raw=\(RequestRouter.hex(bytes))")
            do {
                let raw = try HIDReportNormalizer.normalize(reportID: reportID, bytes: bytes)
                guard let endpoint = endpoints[deviceID] else { return }
                await router.receive(deviceID: endpoint.physicalID, bytes: raw)
                guard raw.count >= 7 else { return }
                let id = "\(endpoint.physicalID)#\(raw[1])"
                guard var route = routes[id] else { return }
                if let notice = ReceiverMessages.parseDeviceConnection(raw), endpoints[deviceID]?.isReceiver == true {
                    let pairingChanged = route.wirelessProductID.map { $0 != notice.wirelessProductID } ?? false
                    let arrived = notice.linkEstablishedAssumed && (route.localLink != true || route.departing || pairingChanged)
                    route.wirelessProductID = notice.wirelessProductID
                    route.revision &+= 1
                    route.localLink = notice.linkEstablishedAssumed
                    if pairingChanged {
                        route.metadataRead = false; route.state.featureIndex = 0
                        route.state.hostCount = 0; route.state.currentHost = nil; route.state.online = false
                        route.state.name = "\(endpoint.name) · 기기 \(route.index)"; route.state.kind = "기기"
                        if selectedKeyboard == id { selectedKeyboard = nil }
                        if selectedMouse == id { selectedMouse = nil }
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
                        if id == selectedKeyboard { cancelDeferred(stage: "CAN_RET"); manualGeneration &+= 1; await coordinator?.cancelAll(reason: "키보드 복귀") }
                    } else if !notice.linkEstablishedAssumed { route.state.online = false }
                    routes[id] = route
                    if arrived || pairingChanged { refreshes[id]?.task.cancel(); refreshes[id] = nil }
                    log.write("[\(id)] 수신기 링크 알림 flags=\(notice.flags) local=\(notice.linkEstablishedAssumed)")
                    publish(); refresh(force: true)
                } else if let frame = HidppParser.parse(raw), frame.funcSwByte == 0 {
                    if route.state.featureIndex == 0 {
                        let evicted = route.early.hold(frame, generation: route.generation, nowMilliseconds: Self.nowMS)
                        routes[id] = route
                        log.write("[\(id)] 초기 자발 신호 보관\(evicted ? "; 이전 신호 한 개 폐기" : "")")
                    } else { await handleDeparture(id, frame: frame) }
                } else if let frame = HidppParser.parse(raw), frame.softwareID != softwareID {
                    log.write("다른 앱 응답 softwareID=\(frame.softwareID)")
                }
            } catch { log.write("리포트 정규화 생략: \(error)") }
        }
    }

    private func handleDeparture(_ id: String, frame: HidppFrame) async {
        guard var route = routes[id] else { return }
        guard let target = ChangeHostFeature.parseHostChangeEvent(frame, featureIndex: route.state.featureIndex) else {
            log.write("[\(id)] 전환 이외 자발 신호: feature/목적지 범위 불일치"); return
        }
        route.departing = true; route.departedAt = Self.nowMS; route.revision &+= 1; routes[id] = route
        log.write("[\(id)] 출발 신호: 채널 \(target + 1); 목적지 도착은 미확인")
        let run = beginRequest()
        activity(run: run, target: target, source: "F", role: "K1", stage: "KEY")
        guard id == selectedKeyboard else { activity(run: run, target: target, source: "F", role: "D1", stage: "SKIP_KEY"); log.write("따라가기 생략: 선택한 키보드 아님"); return }
        guard follow else { activity(run: run, target: target, source: "F", role: "K1", stage: "SKIP_OFF"); log.write("따라가기 생략: 설정 꺼짐"); return }
        guard manualSourceGeneration != route.generation || !manual.matches(generation: manualGeneration, target: target) else {
            activity(run: run, target: target, source: "F", role: "K1", stage: "SKIP_MAN"); log.write("따라가기 생략: 수동 요청 에코"); return
        }
        guard let mouse = selectedMouse, let mouseRoute = routes[mouse] else { activity(run: run, target: target, source: "F", role: "M1", stage: "SKIP_SEL"); log.write("따라가기 생략: 마우스 미선택"); return }
        callback?(.keyboardDeparture(target: target))
        callback?(.notice(run: run, target: target, rows: [noticeRow(id, state: .complete), noticeRow(mouse, state: .switching)].compactMap { $0 }, connection: false))
        cancelDeferred(stage: "CAN_NEW")
        manualGeneration &+= 1
        await coordinator?.cancelAll(reason: "새 키보드 목적지")
        let readiness = FollowPreparation.readiness(exists: true, transport: outputEndpoint(mouseRoute.endpoint) != nil,
                                                     localLink: mouseRoute.localLink, online: mouseRoute.state.online,
                                                     hosts: mouseRoute.state.hostCount, observedLocally: mouseRoute.observedLocally)
        guard readiness != .exclude else { activity(run: run, target: target, source: "F", role: "M1", stage: "SKIP_LINK"); log.write("따라가기 생략: 이 실행에서 마우스 로컬 링크를 확인하지 못함"); return }
        pendingFollow = (run, target)
        if readiness == .wait { activity(run: run, target: target, source: "F", role: "M1", stage: "WAIT") }
        deferredFollow = Task { [weak self] in
            let started = Self.nowMS
            while !Task.isCancelled {
                guard let self else { return }
                if await self.followMouseIfReady(mouse, target: target, run: run) { return }
                if FollowPreparation.expired(started: started, now: Self.nowMS) {
                    await self.followExpired(mouse, run: run, target: target)
                    return
                }
                do { try await Task.sleep(for: .milliseconds(100)) } catch { return }
            }
        }
    }

    private func followMouseIfReady(_ id: String, target: Int, run: Int) async -> Bool {
        guard follow, selectedMouse == id, let route = routes[id] else { return true }
        guard route.state.online, route.localLink != false, route.state.hostCount > 0 else { return false }
        await switchOne(id, target: target, run: run, source: "F", reason: "키보드 출발")
        if pendingFollow?.run == run { pendingFollow = nil }
        return true
    }

    private func noticeRow(_ id: String, state: NoticeState) -> NoticeRow? {
        guard let device = routes[id]?.state else { return nil }
        return NoticeRow(id: id, name: device.name, kind: device.kind, state: state)
    }

    private func switchManualWhenReady(_ id: String, connection: UInt64, generation: UInt64,
                                       target: Int, run: Int, source: String) async {
        let startedAt = Self.nowMS
        let role = id == selectedKeyboard ? "K1" : "M1"
        var waiting = false
        defer { if manualPreparations[id] == generation { manualPreparations[id] = nil } }
        while true {
            guard !Task.isCancelled, started, manualGeneration == generation,
                  id == selectedKeyboard || id == selectedMouse,
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
        let role = device.kind == "키보드" ? "K1" : device.kind == "마우스" ? "M1" : "D1"
        let requestStart = requestStarts[run] ?? Self.nowMS
        _ = await coordinator.switchHost(id, target: target, reason: reason) { [weak self, log] progress in
            log.writeCode(SwitchCode.format(request: run, source: source, target: target, role: role, progress: progress,
                                            requestElapsedMS: Self.nowMS - requestStart))
            let state: NoticeState
            switch progress.outcome {
            case .alreadyCurrent, .targetReported, .localLinkClosed: state = .complete
            case .failed, .stillOnSource: state = .failed
            case .cancelled, .unverified: state = .unconfirmed
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
        if requestStarts.count > 200 { requestStarts[noticeRun - 200] = nil }
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
        pendingFollow = nil
        log.write("따라가기 대기 만료: 10초 동안 마우스 준비 안 됨")
    }

    private func publish() { callback?(.devices(currentDevices())) }
    private static var nowMS: Int64 { Int64(ProcessInfo.processInfo.systemUptime * 1000) }
}
