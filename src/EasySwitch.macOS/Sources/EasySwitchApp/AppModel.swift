import AppKit
import Combine
import SwiftUI
import EasySwitchCore
import EasySwitchEngine
import EasySwitchTransport

typealias DeviceViewState = DeviceState

@MainActor
final class AppModel: ObservableObject {
    @Published var receivers: [ReceiverState] = []
    @Published var pairingProgress: ReceiverPairingProgress?
    private var pairingWindow: NSWindow?
    @Published var devices: [DeviceViewState] = []
    @Published var selectedKeyboardIDs: Set<String> = []
    @Published var selectedMouseIDs: Set<String> = []
    // Compatibility for existing rendering fixtures; production selection uses sets.
    var selectedKeyboardID: String? {
        get { selectedKeyboardIDs.sorted().first }
        set { selectedKeyboardIDs = Set([newValue].compactMap { $0 }) }
    }
    var selectedMouseID: String? {
        get { selectedMouseIDs.sorted().first }
        set { selectedMouseIDs = Set([newValue].compactMap { $0 }) }
    }
    @Published var followEnabled = false
    @Published var logs: [String] = []
    @Published var status = "연결된 기기를 검색하고 있어"
    @Published var scanInProgress = false
    @Published var needsInputMonitoring = false
    @Published var floatingEnabled = false { didSet { floatingChanged() } }
    @Published var floatingTransparency = 0 { didSet { floatingChanged() } }
    @Published var floatingSize = 100 { didSet { floatingChanged() } }
    @Published var floatingAnimationSpeed = 100 { didSet { floatingChanged() } }
    @Published var floatingAlwaysShow = false { didSet { floatingChanged() } }
    @Published var bounceEnabled = true { didSet { floatingChanged() } }
    @Published var toastEnabled = true { didSet { toastChanged() } }
    @Published var toastSize = 100 { didSet { toastChanged() } }
    @Published var toastTransparency = 0 { didSet { toastChanged() } }
    @Published var exportEnabled = false { didSet { mirrorChanged() } }
    @Published var exportDirectory: URL?
    @Published var language = "ko"
    @Published var channelLabels = ["", "", ""]
    @Published var showCodeLogs = false
    @Published var codeLogs: [String] = []
    let pageRequests = PassthroughSubject<AppPage, Never>()
    var onStarted: (() -> Void)?
    var onShowMainWindow: (() -> Void)?
    var onLanguageChanged: (() -> Void)?
    var floatingSizeScreenBounds: NSRect?
    let mainWindowViewState = MainWindowViewState()
    private var mainWindowVisible = false
    @Published var relayEnabled = false { didSet { if !loadingPreferences { defaults.set(relayEnabled, forKey: "relayEnabled"); configureRelay() } } }
    private var relayID = UUID()
    private var hotkeys: GlobalHotkeys?
    @Published var hotkeysEnabled = true { didSet { if !loadingPreferences { defaults.set(hotkeysEnabled, forKey: "hotkeysEnabled"); hotkeys?.configure(enabled: hotkeysEnabled) } } }
    private var floating: FloatingController?
    private var notices: NoticePresenter?
    private var floatingX = 0.88, floatingY = 0.58
    private var floatingScreen: String?
    private var loadingPreferences = true
    private var mirrorRevision = 0
    private var scopedFolder: URL?
    let logDirectory: URL
    let log: FileLog
    @Published var systemEngineEnabled = false
    private var engine: (any EngineRuntime)?
    private var updates: Task<Void, Never>?
    private var wakeObserver: NSObjectProtocol?
    private let fixture: Bool
    private let defaults: UserDefaults

    init(log: FileLog, fixture: Bool = false, preferences: UserDefaults? = nil) {
        self.log = log; self.logDirectory = log.directory; self.fixture = fixture
        defaults = preferences ?? (fixture ? UserDefaults(suiteName: "EasySwitch.RenderingFixture")! : .standard)
        if !fixture {
            selectedKeyboardIDs = Set(defaults.stringArray(forKey: "selectedKeyboardIDs") ?? [defaults.string(forKey: "selectedKeyboardID")].compactMap { $0 })
            selectedMouseIDs = Set(defaults.stringArray(forKey: "selectedMouseIDs") ?? [defaults.string(forKey: "selectedMouseID")].compactMap { $0 })
            followEnabled = defaults.bool(forKey: "followEnabled")
            systemEngineEnabled = defaults.bool(forKey: "systemEngineEnabled")
            relayEnabled = defaults.bool(forKey: "relayEnabled")
            relayID = defaults.string(forKey: "relayID").flatMap(UUID.init(uuidString:)) ?? UUID()
            defaults.set(relayID.uuidString, forKey: "relayID")
            hotkeysEnabled = defaults.object(forKey: "hotkeysEnabled") as? Bool ?? true
            floatingEnabled = defaults.bool(forKey: "floatingEnabled")
            floatingTransparency = max(0, min(100, defaults.integer(forKey: "floatingTransparency")))
            floatingSize = defaults.object(forKey: "floatingSize").map { _ in max(0, min(200, defaults.integer(forKey: "floatingSize"))) } ?? 100
            floatingAnimationSpeed = defaults.object(forKey: "floatingAnimationSpeed").map { _ in max(25, min(300, defaults.integer(forKey: "floatingAnimationSpeed"))) } ?? 100
            floatingAlwaysShow = defaults.bool(forKey: "floatingAlwaysShow")
            bounceEnabled = defaults.object(forKey: "bounceEnabled").map { _ in defaults.bool(forKey: "bounceEnabled") } ?? true
            let toast = ToastPreferences(defaults: defaults)
            toastEnabled = toast.enabled; toastSize = toast.size; toastTransparency = toast.transparency
            language = defaults.string(forKey: "language") == "en" ? "en" : "ko"
            channelLabels = (defaults.stringArray(forKey: "channelLabels") ?? ["", "", ""]).prefix(3).map { $0 }
            while channelLabels.count < 3 { channelLabels.append("") }
            floatingX = defaults.object(forKey: "floatingX").map { _ in defaults.double(forKey: "floatingX") } ?? 0.88
            floatingY = defaults.object(forKey: "floatingY").map { _ in defaults.double(forKey: "floatingY") } ?? 0.58
            floatingScreen = defaults.string(forKey: "floatingScreen")
            exportDirectory = defaults.string(forKey: "exportDirectory").map { URL(fileURLWithPath: $0, isDirectory: true) }
            if let bookmark = defaults.data(forKey: "exportBookmark") {
                var stale = false
                if let resolved = try? URL(resolvingBookmarkData: bookmark, options: .withSecurityScope, bookmarkDataIsStale: &stale) {
                    exportDirectory = resolved
                }
            }
            exportEnabled = defaults.bool(forKey: "exportEnabled") && exportDirectory != nil
        }
        loadingPreferences = false
    }

    static func verifySelectionMigration(log: FileLog) -> [String: Bool] {
        let domain = "EasySwitch.SelectionFixture." + UUID().uuidString
        let preferences = UserDefaults(suiteName: domain)!
        defer { preferences.removePersistentDomain(forName: domain) }
        preferences.set("keyboard-one", forKey: "selectedKeyboardID")
        preferences.set("mouse-one", forKey: "selectedMouseID")
        let model = AppModel(log: log, preferences: preferences)
        var checks = ["legacyKeyboardRestores": model.selectedKeyboardIDs == ["keyboard-one"],
                      "legacyMouseRestores": model.selectedMouseIDs == ["mouse-one"]]
        model.toggleSelection(.init(id: "mouse-two", name: "Second mouse", connection: "fixture", kind: "마우스", online: true, currentHost: 0, hostCount: 3, featureIndex: 9))
        checks["multipleMicePersist"] = Set(preferences.stringArray(forKey: "selectedMouseIDs") ?? []) == ["mouse-one", "mouse-two"]
        let restored = AppModel(log: log, preferences: preferences)
        checks["multipleMiceRestore"] = restored.selectedMouseIDs == ["mouse-one", "mouse-two"]
        restored.receive(.selectionInvalidated("mouse-one"))
        checks["invalidationKeepsOtherMouse"] = restored.selectedMouseIDs == ["mouse-two"]
        restored.selectMouse(nil)
        let cleared = AppModel(log: log, preferences: preferences)
        checks["emptySelectionDoesNotReviveLegacy"] = cleared.selectedMouseIDs.isEmpty
        checks["keyboardUnaffected"] = cleared.selectedKeyboardIDs == ["keyboard-one"]
        return checks
    }

    func start() {
        guard !fixture, engine == nil else { return }
        let runtime: any EngineRuntime = systemEngineEnabled ? ServiceEngine(log: log) : DeviceEngine(log: log)
        engine = runtime
        hotkeys = GlobalHotkeys(action: { [weak self] channel in self?.switchChannel(channel, source: "H") }, log: { [log] in log.write($0) })
        hotkeys?.configure(enabled: hotkeysEnabled)
        notices = NoticePresenter(preferences: toastPreferences)
        floating = FloatingController(onAction: { [weak self] action in
            guard let self else { return }
            if action == 3 { self.onShowMainWindow?() }
            else { self.switchChannel(action + 1, source: "P") }
        }, onHidden: { [weak self] in self?.floatingEnabled = false }, onMoved: { [weak self] x, y, screen in
            guard let self else { return }
            self.floatingX = x; self.floatingY = y; self.floatingScreen = screen
            self.savePreferences()
        })
        floating?.onDiagnostic = { [log] in log.write($0) }
        floating?.outsideDismissExclusion = { [weak self] point in self?.floatingSizeScreenBounds?.contains(point) ?? false }
        updateFloating()
        configureRelay()
        mirrorChanged()
        Task { [self, runtime] in
            do {
                // Restore the saved pair before the first HID arrival or early switch event.
                await runtime.configure(keyboardIDs: selectedKeyboardIDs, mouseIDs: selectedMouseIDs, followEnabled: followEnabled)
                guard engine === runtime else { return }
                try await runtime.start { [weak self, weak runtime] event in
                    Task { @MainActor [weak self, weak runtime] in
                        guard let self, let runtime, self.engine === runtime else { return }
                        self.receive(event)
                    }
                }
                guard engine === runtime else { await runtime.stop(); return }
                await configure()
                onStarted?()
            } catch {
                guard engine === runtime else { return }
                onStarted?()
                status = "기기 감시를 시작하지 못했어"; log.write("엔진 시작 실패: \(error)")
            }
        }
        beginMainWindowUpdates()
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification,
                                                                         object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in await self?.restartAfterWake() }
        }
    }

    func setMainWindowVisible(_ visible: Bool) {
        mainWindowVisible = visible
        beginMainWindowUpdates()
        if !visible { logs = []; codeLogs = []; floatingSizeScreenBounds = nil }
    }

    private func beginMainWindowUpdates() {
        updates?.cancel(); updates = nil
        guard mainWindowVisible, !fixture else { return }
        updates = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                let lines = await self.log.recentLines()
                let codes = await self.log.recentCodes()
                guard self.mainWindowVisible, !Task.isCancelled else { return }
                if self.logs != lines { self.logs = lines }
                if self.codeLogs != codes { self.codeLogs = codes }
                do { try await Task.sleep(for: .milliseconds(500)) } catch { return }
            }
        }
    }

    func stop() async {
        updates?.cancel(); updates = nil
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        wakeObserver = nil
        hotkeys?.stop(); hotkeys = nil
        floating?.stop(); floating = nil
        notices?.stop(); notices = nil
        await engine?.stop(); engine = nil
    }

    private func restartAfterWake() async {
        log.write("절전 복귀: 이전 요청 취소 후 HID 경로 재구성")
        await stop()
        start()
    }

    private func receive(_ event: EngineEvent) {
        switch event {
        case .devices(let devices):
            if self.devices != devices { self.devices = devices }
            let status = devices.contains(where: \.online) ? "연결된 기기를 선택해 줘" : "연결된 전환 기기가 없음"
            if self.status != status { self.status = status }
        case .scanning(let scanning): if scanInProgress != scanning { scanInProgress = scanning }
        case .status(let status): if self.status != status { self.status = status }
        case .selectionInvalidated(let id):
            selectedKeyboardIDs.remove(id); selectedMouseIDs.remove(id)
            persistSelections()
        case .inputMonitoringRequired: needsInputMonitoring = true
        case .notice(let run, let target, let rows, let connection):
            notices?.show(run: run, target: target, rows: rows, connection: connection, english: language == "en", reservedNames: devices.map(\.name))
        case .keyboardDeparture(let target): floating?.keyboardFeedback(target: target)
        case .keyboardArrival: break // The active local/system engine owns authenticated relay delivery.
        case .receivers(let receivers): if self.receivers != receivers { self.receivers = receivers }
        case .pairing(let progress): pairingProgress = progress
        }
    }

    func setSystemEngineEnabled(_ enabled: Bool) {
        guard !fixture else { return }
        Task {
            await stop()
            do {
                let ready = try SystemEngineRegistration.configure(enabled: enabled)
                systemEngineEnabled = enabled; defaults.set(enabled, forKey: "systemEngineEnabled")
                log.write("로그인 전 엔진 설정: enabled=\(enabled), approved=\(ready)")
                start()
                if enabled && !ready { status = t("macOS 로그인 항목에서 EasySwitch를 승인한 뒤 앱을 다시 열어 줘.", "Approve EasySwitch in macOS Login Items, then reopen the app.") }
            } catch { log.write("로그인 전 엔진 등록 변경 실패: \(error)"); start() }
        }
    }

    func showReceiverPairing() {
        if let pairingWindow { pairingWindow.makeKeyAndOrderFront(nil); return }
        let window = NSWindow(contentRect: .init(x: 0, y: 0, width: 460, height: 460), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = t("새 기기 연결", "Connect a device"); window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: .darkAqua)
        window.contentView = NSHostingView(rootView: ReceiverPairingView(model: self)); window.center()
        pairingWindow = window; window.makeKeyAndOrderFront(nil)
    }
    func beginPairing(_ receiverID: String) {
        pairingProgress = .init(receiverID: receiverID, stage: "searching")
        Task { await engine?.beginPairing(receiverID) }
    }
    func confirmPairing(_ receiverID: String) { Task { await engine?.confirmPairing(receiverID) } }
    func cancelPairing(_ receiverID: String) { Task { await engine?.cancelPairing(receiverID) } }

    private func configureRelay() {
        guard !fixture, let engine else { return }
        do {
            let secret = relayEnabled ? try RelaySecretStore.load() : nil
            if relayEnabled && secret == nil { log.write("LAN 중계 대기: 먼저 태블릿 짝 맺기 필요") }
            Task { await engine.configureLAN(id: relayID, secret: secret) }
        } catch { log.write("LAN 중계 설정 실패: \(error)") }
    }

    func pairTablet() {
        guard !fixture else { return }
        do {
            let alert = NSAlert()
            alert.messageText = t("태블릿과 새로 짝 맺기", "Pair a tablet")
            alert.informativeText = t("앱이 새 연결 키를 만들면 이전 태블릿 연결은 해제돼. 계속하려면 새 연결 만들기를 눌러 줘.", "Creating a new connection disconnects the previous tablet. Choose Create connection to continue.")
            alert.addButton(withTitle: t("새 연결 만들기", "Create connection")); alert.addButton(withTitle: t("취소", "Cancel"))
            guard alert.runModal() == .alertFirstButtonReturn else { return }
            let secret = try RelaySecretStore.replace()
            let host = Host.current().addresses.first { address in
                address.hasPrefix("192.168.") || address.hasPrefix("10.") || (address.hasPrefix("172.") && (16...31).contains(Int(address.split(separator: ".").dropFirst().first ?? "") ?? 0))
            } ?? "\(Host.current().localizedName ?? "localhost").local"
            var uri = URLComponents(); uri.scheme = "easyswitch"; uri.host = "pair"
            uri.queryItems = [.init(name: "v", value: "1"), .init(name: "host", value: host), .init(name: "port", value: "39821"),
                .init(name: "id", value: relayID.uuidString), .init(name: "key", value: secret.base64EncodedString())]
            relayEnabled = true
            let code = NSAlert(); code.messageText = t("태블릿에 연결 코드 입력", "Enter the connection code on your tablet")
            code.informativeText = t("아래 코드를 복사해서 태블릿 EasySwitch의 PC 연결에 붙여 넣어 줘. 같은 네트워크에서 앱이 자동으로 연결을 확인해.", "Copy this code into PC connection in EasySwitch on your tablet. Both devices connect automatically on the same network.")
            let field = NSTextField(wrappingLabelWithString: uri.string ?? "")
            field.isSelectable = true; field.frame = .init(x: 0, y: 0, width: 420, height: 100); code.accessoryView = field
            code.addButton(withTitle: t("복사", "Copy")); code.addButton(withTitle: t("닫기", "Close"))
            if code.runModal() == .alertFirstButtonReturn { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(uri.string ?? "", forType: .string) }
        } catch { log.write("태블릿 짝 맺기 실패: \(error)") }
    }

    func refresh() {
        guard !fixture else { return }
        Task { await engine?.refresh(force: true) }
    }
    func requestInputMonitoring() {
        guard !fixture else { return }
        if HIDMonitor.requestInputMonitoringAccess() { reopenHID() }
        else if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent") {
            NSWorkspace.shared.open(url)
        }
    }

    func reopenHID() {
        guard !fixture else { return }
        let previouslyDenied = needsInputMonitoring
        Task {
            await stop()
            // Retrying with no currently attached device does not prove the prior denial was resolved.
            needsInputMonitoring = previouslyDenied && HIDMonitor.inputMonitoringAccess() != .granted
            start()
        }
    }
    func switchChannel(_ channel: Int, source: String = "B") {
        guard !fixture else { return }
        guard let runtime = engine else { return }
        Task { [weak self, weak runtime] in
            await runtime?.switchChannel(channel, source: source, presentNotice: { [weak self, weak runtime] event in
                await MainActor.run {
                    guard let self, let runtime, self.engine === runtime else { return }
                    self.receive(event)
                }
            })
        }
    }

    func setFollowEnabled(_ value: Bool) {
        followEnabled = value
        guard !fixture else { return }
        defaults.set(value, forKey: "followEnabled")
        Task { await configure() }
    }

    func selectKeyboard(_ id: String?) {
        selectedKeyboardIDs = Set([id].compactMap { $0 }); persistSelections()
    }
    func selectMouse(_ id: String?) {
        selectedMouseIDs = Set([id].compactMap { $0 }); persistSelections()
    }
    func toggleSelection(_ device: DeviceViewState) {
        if device.kind == "키보드" {
            if !selectedKeyboardIDs.insert(device.id).inserted { selectedKeyboardIDs.remove(device.id) }
        } else if device.kind == "마우스" {
            if !selectedMouseIDs.insert(device.id).inserted { selectedMouseIDs.remove(device.id) }
        }
        persistSelections()
    }
    private func persistSelections() {
        guard !fixture else { return }
        defaults.set(selectedKeyboardIDs.sorted(), forKey: "selectedKeyboardIDs")
        defaults.set(selectedMouseIDs.sorted(), forKey: "selectedMouseIDs")
        // Keep downgrade-compatible primary IDs without discarding the complete sets.
        defaults.set(selectedKeyboardID, forKey: "selectedKeyboardID")
        defaults.set(selectedMouseID, forKey: "selectedMouseID")
        Task { await configure() }
    }

    private func configure() async {
        await engine?.configure(keyboardIDs: selectedKeyboardIDs, mouseIDs: selectedMouseIDs, followEnabled: followEnabled)
    }

    func showLogsInFinder() { NSWorkspace.shared.open(logDirectory) }
    func t(_ ko: String, _ en: String) -> String { language == "en" ? en : ko }
    func setLanguage(_ value: String) {
        language = value == "en" ? "en" : "ko"
        savePreferences(); onLanguageChanged?()
    }
    func channelLabel(_ channel: Int) -> String {
        let name = channelLabels.indices.contains(channel - 1) ? channelLabels[channel - 1] : ""
        return name.isEmpty ? t("채널 \(channel)", "Channel \(channel)") : name
    }
    func renameChannel(_ channel: Int, name: String) {
        guard channelLabels.indices.contains(channel - 1) else { return }
        channelLabels[channel - 1] = String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(80))
        savePreferences()
    }

    private func floatingChanged() {
        guard !loadingPreferences else { return }
        savePreferences(); updateFloating()
    }
    private var toastPreferences: ToastPreferences {
        ToastPreferences(enabled: toastEnabled, size: toastSize, transparency: toastTransparency)
    }
    private func toastChanged() {
        guard !loadingPreferences else { return }
        savePreferences(); notices?.configure(toastPreferences)
    }
    private func updateFloating() {
        guard !fixture else { return }
        floating?.update(enabled: floatingEnabled, transparency: max(0, min(100, floatingTransparency)),
                         size: max(0, min(200, floatingSize)), speed: max(25, min(300, floatingAnimationSpeed)),
                         alwaysShow: floatingAlwaysShow, bounce: bounceEnabled, x: floatingX, y: floatingY, screenID: floatingScreen)
    }
    private func savePreferences() {
        guard !fixture, !loadingPreferences else { return }
        toastPreferences.save(to: defaults)
        for (key, value) in ["floatingEnabled": floatingEnabled, "floatingTransparency": floatingTransparency,
                             "floatingSize": floatingSize, "floatingAnimationSpeed": floatingAnimationSpeed,
                             "floatingAlwaysShow": floatingAlwaysShow, "bounceEnabled": bounceEnabled,
                             "language": language, "channelLabels": channelLabels, "floatingX": floatingX,
                             "floatingY": floatingY] as [String: Any] { defaults.set(value, forKey: key) }
        defaults.set(floatingScreen, forKey: "floatingScreen")
        defaults.set(exportDirectory?.path, forKey: "exportDirectory")
        defaults.set(exportEnabled, forKey: "exportEnabled")
    }

    func chooseExportFolder() { chooseExportFolder(enableAfter: false) }
    private func chooseExportFolder(enableAfter: Bool) {
        guard !fixture else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.message = t("앱이 이 폴더에 로그 사본을 추가로 기록해요.", "EasySwitch writes an additional copy of its logs to this folder.")
        panel.begin { [weak self] response in
            guard let self else { return }
            guard response == .OK, let directory = panel.url else {
                if enableAfter { self.exportEnabled = false }
                return
            }
            self.exportDirectory = directory
            if let bookmark = try? directory.bookmarkData(options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil) {
                self.defaults.set(bookmark, forKey: "exportBookmark")
            }
            if enableAfter { self.exportEnabled = true }
            else { self.mirrorChanged() }
            self.savePreferences()
        }
    }
    func clearExportFolder() {
        exportEnabled = false; exportDirectory = nil
        defaults.removeObject(forKey: "exportBookmark"); savePreferences()
    }
    private func mirrorChanged() {
        guard !fixture, !loadingPreferences else { return }
        if exportEnabled && exportDirectory == nil { chooseExportFolder(enableAfter: true); return }
        mirrorRevision += 1
        let revision = mirrorRevision
        let folder = exportEnabled ? exportDirectory : nil
        scopedFolder?.stopAccessingSecurityScopedResource()
        scopedFolder = folder
        _ = scopedFolder?.startAccessingSecurityScopedResource()
        savePreferences()
        Task {
            do { try await log.setMirror(directory: folder, revision: revision) }
            catch {
                log.write("외부 로그 저장 변경 실패: \(error)")
                if revision == mirrorRevision { exportEnabled = false }
            }
        }
    }

    func exportLogs() { exportFile(codes: showCodeLogs) }
    func exportActivityLogs() { exportFile(codes: false) }
    private func exportFile(codes: Bool) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = (codes ? log.codeURL : log.fileURL).lastPathComponent
        panel.allowedContentTypes = [.plainText]
        panel.begin { [weak self] response in
            guard response == .OK, let destination = panel.url, let self else { return }
            Task {
                do { try await self.log.export(to: destination, codes: codes); self.status = self.t("로그를 저장했어", "Log exported") }
                catch { self.status = "로그를 저장하지 못했어"; self.log.write("로그 내보내기 실패: \(error)") }
            }
        }
    }
}
