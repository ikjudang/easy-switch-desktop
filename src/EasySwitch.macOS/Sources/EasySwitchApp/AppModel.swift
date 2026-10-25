import AppKit
import Combine
import EasySwitchEngine
import EasySwitchTransport

typealias DeviceViewState = DeviceState

@MainActor
final class AppModel: ObservableObject {
    @Published var devices: [DeviceViewState] = []
    @Published var selectedKeyboardID: String?
    @Published var selectedMouseID: String?
    @Published var followEnabled = false
    @Published var showRawLogs = false
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
    @Published var exportEnabled = false { didSet { mirrorChanged() } }
    @Published var exportDirectory: URL?
    @Published var language = "ko"
    @Published var channelLabels = ["", "", ""]
    @Published var showCodeLogs = false
    @Published var codeLogs: [String] = []
    let pageRequests = PassthroughSubject<AppPage, Never>()
    var onShowMainWindow: (() -> Void)?
    var onLanguageChanged: (() -> Void)?
    var floatingSizeScreenBounds: NSRect?
    private var floating: FloatingController?
    private var notices: NoticePresenter?
    private var floatingX = 0.88, floatingY = 0.58
    private var floatingScreen: String?
    private var loadingPreferences = true
    private var mirrorRevision = 0
    private var scopedFolder: URL?
    let logDirectory: URL
    let log: FileLog
    private var engine: DeviceEngine?
    private var updates: Task<Void, Never>?
    private var wakeObserver: NSObjectProtocol?
    private let fixture: Bool
    private let defaults: UserDefaults

    init(log: FileLog, fixture: Bool = false) {
        self.log = log; self.logDirectory = log.directory; self.fixture = fixture
        defaults = fixture ? UserDefaults(suiteName: "EasySwitch.RenderingFixture")! : .standard
        if !fixture {
            selectedKeyboardID = defaults.string(forKey: "selectedKeyboardID")
            selectedMouseID = defaults.string(forKey: "selectedMouseID")
            followEnabled = defaults.bool(forKey: "followEnabled")
            floatingEnabled = defaults.bool(forKey: "floatingEnabled")
            floatingTransparency = max(0, min(100, defaults.integer(forKey: "floatingTransparency")))
            floatingSize = defaults.object(forKey: "floatingSize").map { _ in max(0, min(200, defaults.integer(forKey: "floatingSize"))) } ?? 100
            floatingAnimationSpeed = defaults.object(forKey: "floatingAnimationSpeed").map { _ in max(25, min(300, defaults.integer(forKey: "floatingAnimationSpeed"))) } ?? 100
            floatingAlwaysShow = defaults.bool(forKey: "floatingAlwaysShow")
            bounceEnabled = defaults.object(forKey: "bounceEnabled").map { _ in defaults.bool(forKey: "bounceEnabled") } ?? true
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

    func start() {
        guard !fixture, engine == nil else { return }
        let runtime = DeviceEngine(log: log)
        engine = runtime
        notices = NoticePresenter(log: log)
        floating = FloatingController(onAction: { [weak self] action in
            guard let self else { return }
            if action == 3 { self.onShowMainWindow?() }
            else { self.switchChannel(action + 1, source: "P") }
        }, onHidden: { [weak self] in self?.floatingEnabled = false }, onMoved: { [weak self] x, y, screen in
            guard let self else { return }
            self.floatingX = x; self.floatingY = y; self.floatingScreen = screen
            self.savePreferences()
        })
        floating?.outsideDismissExclusion = { [weak self] point in self?.floatingSizeScreenBounds?.contains(point) ?? false }
        updateFloating()
        mirrorChanged()
        Task { [self, runtime] in
            do {
                // Restore the saved pair before the first HID arrival or early switch event.
                await runtime.configure(keyboardID: selectedKeyboardID, mouseID: selectedMouseID, followEnabled: followEnabled)
                guard engine === runtime else { return }
                try await runtime.start { [weak self, weak runtime] event in
                    Task { @MainActor [weak self, weak runtime] in
                        guard let self, let runtime, self.engine === runtime else { return }
                        self.receive(event)
                    }
                }
                guard engine === runtime else { await runtime.stop(); return }
                await configure()
            } catch {
                guard engine === runtime else { return }
                status = "기기 감시를 시작하지 못했어"; log.write("엔진 시작 실패: \(error)")
            }
        }
        updates = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                self.logs = await self.log.recentLines()
                self.codeLogs = await self.log.recentCodes()
                do { try await Task.sleep(for: .milliseconds(500)) } catch { return }
            }
        }
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification,
                                                                         object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in await self?.restartAfterWake() }
        }
    }

    func stop() async {
        updates?.cancel(); updates = nil
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        wakeObserver = nil
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
            self.devices = devices
            status = devices.contains(where: \.online) ? "연결된 기기를 선택해 줘" : "연결된 전환 기기가 없음"
        case .scanning(let scanning): scanInProgress = scanning
        case .status(let status): self.status = status
        case .selectionInvalidated(let id):
            if selectedKeyboardID == id { selectKeyboard(nil) }
            if selectedMouseID == id { selectMouse(nil) }
        case .inputMonitoringRequired: needsInputMonitoring = true
        case .notice(let run, let target, let rows, let connection):
            notices?.show(run: run, target: target, rows: rows, connection: connection, english: language == "en", reservedNames: devices.map(\.name))
        case .keyboardDeparture(let target): floating?.keyboardFeedback(target: target)
        }
    }

    func refresh() {
        guard !fixture else { return }
        log.write("사용자 요청: 기기 새로고침")
        Task { await engine?.refresh(force: true) }
    }
    func requestInputMonitoring() {
        guard !fixture else { return }
        log.write("사용자 요청: 입력 모니터링 권한 안내 열기")
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
        selectedKeyboardID = id
        guard !fixture else { return }
        defaults.set(id, forKey: "selectedKeyboardID")
        Task { await configure() }
    }

    func selectMouse(_ id: String?) {
        selectedMouseID = id
        guard !fixture else { return }
        defaults.set(id, forKey: "selectedMouseID")
        Task { await configure() }
    }

    private func configure() async {
        await engine?.configure(keyboardID: selectedKeyboardID, mouseID: selectedMouseID, followEnabled: followEnabled)
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
    private func updateFloating() {
        guard !fixture else { return }
        floating?.update(enabled: floatingEnabled, transparency: max(0, min(100, floatingTransparency)),
                         size: max(0, min(200, floatingSize)), speed: max(25, min(300, floatingAnimationSpeed)),
                         alwaysShow: floatingAlwaysShow, bounce: bounceEnabled, x: floatingX, y: floatingY, screenID: floatingScreen)
    }
    private func savePreferences() {
        guard !fixture, !loadingPreferences else { return }
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
