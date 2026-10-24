import AppKit
import CoreText
import CoreServices
import EasySwitchEngine
import EasySwitchTransport
import Darwin

@main
struct EasySwitchMain {
    @MainActor static func main() async {
        let args = CommandLine.arguments
        if args.contains("--probe") { Darwin.exit(await runProbe(args)) }
        let app = NSApplication.shared
        app.setActivationPolicy(args.contains("--render") || args.contains("--render-icon") ? .regular : .accessory)
        registerFonts()
        let delegate = AppDelegate(arguments: args)
        app.delegate = delegate
        app.run()
        withExtendedLifetime(delegate) {}
    }

    @MainActor private static func registerFonts() {
        guard let directory = Bundle.module.url(forResource: "Resources", withExtension: nil)?.appendingPathComponent("Fonts"),
              let urls = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) else { return }
        for url in urls { CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil) }
    }

    private static func runProbe(_ args: [String]) async -> Int32 {
        do {
            let log = try FileLog()
            let seconds = args.firstIndex(of: "--seconds").flatMap { index in
                index + 1 < args.count ? Double(args[index + 1]) : nil
            } ?? 6
            guard seconds.isFinite, seconds > 0, seconds <= 300 else {
                FileHandle.standardError.write(Data("--seconds 범위: 0 초과 300 이하\n".utf8)); return 2
            }
            print("EasySwitch macOS 읽기 전용 probe")
            print("Input Monitoring: \(HIDMonitor.inputMonitoringAccess().rawValue)")
            print("로그: \(log.fileURL.path)")
            let driver = HIDMonitor()
            let engine = DeviceEngine(driver: driver, log: log)
            try await engine.start { _ in }
            try await Task.sleep(for: .seconds(seconds))
            let endpoints = await driver.snapshot()
            let devices = await engine.currentDevices()
            await engine.stop()
            print("Logitech HID: \(endpoints.count), HID++ 열린 통로: \(endpoints.filter(\.isOpen).count), 준비된 기기: \(devices.filter(\.online).count)")
            print("채널 전환·수신기 설정 쓰기: 0회")
            return 0
        } catch { FileHandle.standardError.write(Data("probe 실패: \(error)\n".utf8)); return 1 }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let arguments: [String]
    private var model: AppModel?
    private var controller: AppWindowController?
    private var statusItem: NSStatusItem?
    private var terminating = false

    init(arguments: [String]) { self.arguments = arguments }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let background = LoginStartup.startsInBackground(arguments: arguments, event: NSAppleEventManager.shared().currentAppleEvent)
        do {
            if let index = arguments.firstIndex(of: "--render-icon"), index + 1 < arguments.count {
                try AppIcon.writePNG(to: URL(fileURLWithPath: arguments[index + 1]))
                NSApp.terminate(nil); return
            }
            if let index = arguments.firstIndex(of: "--render"), index + 1 < arguments.count {
                Task {
                    do { try await renderFixtures(to: URL(fileURLWithPath: arguments[index + 1], isDirectory: true)) }
                    catch { FileHandle.standardError.write(Data("fixture 실패: \(error)\n".utf8)); Darwin.exit(1) }
                    NSApp.terminate(nil)
                }
                return
            }
            let log = try FileLog()
            let model = AppModel(log: log)
            self.model = model
            model.onShowMainWindow = { [weak self] in self?.showMainWindow() }
            model.onLanguageChanged = { [weak self] in self?.buildMenu() }
            controller = AppWindowController(model: model)
            controller?.onClose = { NSApp.setActivationPolicy(.accessory) }
            buildMenu()
            presentAtLaunch(inBackground: background)
            log.write(background ? "백그라운드 시작: 메뉴 막대 상주, 메인 창 표시 안 함" : "사용자 실행: 메인 창 표시")
            model.start()
            LoginStartup.registerIfNeeded(log: log)
        } catch {
            if arguments.contains("--render") || arguments.contains("--render-icon") {
                FileHandle.standardError.write(Data("fixture 실패: \(error)\n".utf8)); Darwin.exit(1)
            }
            if background {
                FileHandle.standardError.write(Data("EasySwitch 백그라운드 시작 실패: \(error)\n".utf8))
                NSApp.terminate(nil); return
            }
            let alert = NSAlert()
            alert.messageText = "EasySwitch를 시작하지 못했어"
            alert.informativeText = error.localizedDescription
            alert.runModal()
            NSApp.terminate(nil)
        }
    }

    private func buildMenu() {
        func t(_ ko: String, _ en: String) -> String { model?.t(ko, en) ?? ko }
        let mainMenu = NSMenu()
        let appItem = NSMenuItem(); let appMenu = NSMenu()
        appMenu.addItem(withTitle: t("EasySwitch 종료", "Quit EasySwitch"), action: #selector(quit), keyEquivalent: "q").target = self
        appItem.submenu = appMenu; mainMenu.addItem(appItem)
        let editItem = NSMenuItem(); let editMenu = NSMenu(title: t("편집", "Edit"))
        editMenu.addItem(withTitle: t("복사", "Copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: t("붙여넣기", "Paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: t("모두 선택", "Select All"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu; mainMenu.addItem(editItem)
        NSApp.mainMenu = mainMenu

        let item = statusItem ?? NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = StatusBarIcon.makeImage()
        item.button?.target = self
        item.button?.action = #selector(statusItemClicked(_:))
        item.button?.sendAction(on: .leftMouseUp)
        item.button?.toolTip = t("클릭: 플로팅 버튼 켜기", "Click: show floating button")
        item.menu = nil
        statusItem = item
    }

    @objc private func statusItemClicked(_ sender: NSStatusBarButton) {
        handleStatusItemClick()
    }

    private func handleStatusItemClick() {
        // Use the settings binding so enabling also updates the UI and saved preference.
        // Repeated clicks keep the feature enabled rather than toggling it off.
        model?.floatingEnabled = true
        model?.log.write("메뉴 막대 클릭: 플로팅 버튼 표시 요청")
    }

    private func presentAtLaunch(inBackground: Bool) {
        if inBackground { NSApp.setActivationPolicy(.accessory) }
        else { showMainWindow() }
    }

    @objc private func showMainWindow() {
        NSApp.setActivationPolicy(.regular)
        controller?.showWindow(nil); controller?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
    @objc private func quit() { NSApp.terminate(nil) }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showMainWindow(); return false
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let model, !terminating else { return .terminateNow }
        terminating = true
        Task { await model.stop(); NSApp.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }

    private func renderFixtures(to directory: URL) async throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let model = AppModel(log: try FileLog(directory: directory.appendingPathComponent("fixture-logs")), fixture: true)
        model.devices = [
            DeviceState(id: "fixture-keyboard", name: "MX KEYS S", connection: "Bolt", kind: "키보드", online: true, currentHost: 0, hostCount: 3, featureIndex: 9),
            DeviceState(id: "fixture-mouse", name: "MX Master 3S", connection: "Bluetooth", kind: "마우스", online: true, currentHost: 0, hostCount: 3, featureIndex: 10)
        ]
        model.selectedKeyboardID = "fixture-keyboard"; model.selectedMouseID = "fixture-mouse"; model.followEnabled = true
        model.status = "화면 검증용 예시 기기"
        model.logs = ["2026-10-24T10:00:00.123+09:00 화면 검증용 합성 로그", "2026-10-24T10:00:00.456+09:00 연결됨: MX KEYS S · 채널 1"]
        for page in [AppPage.home, .devices, .settings, .logs] {
            let window = AppWindowController(model: model, initialPage: page, storesFrame: false)
            window.window?.orderFront(nil)
            try await Task.sleep(for: .milliseconds(200))
            try window.capture(to: directory.appendingPathComponent("\(page.rawValue).png"))
            window.window?.orderOut(nil)
        }
        let empty = AppModel(log: model.log, fixture: true)
        let emptyWindow = AppWindowController(model: empty, storesFrame: false)
        emptyWindow.window?.orderFront(nil)
        try await Task.sleep(for: .milliseconds(200))
        try emptyWindow.capture(to: directory.appendingPathComponent("home-empty.png"), size: .init(width: 860, height: 600))
        emptyWindow.window?.orderOut(nil)
        for language in ["ko", "en"] {
            model.setLanguage(language)
            for enabled in [false, true] {
                model.floatingEnabled = enabled
                for width in [CGFloat(860), 1140] {
                    let settings = AppWindowController(model: model, initialPage: .logs, storesFrame: false,
                                                       settingsExpanded: true, exportExpanded: true)
                    settings.window?.orderFront(nil)
                    try await Task.sleep(for: .milliseconds(220))
                    try settings.capture(to: directory.appendingPathComponent("settings-\(language)-\(enabled ? "on" : "off")-\(Int(width)).png"),
                                         size: .init(width: width, height: 720))
                    settings.scrollSettingsToBottom()
                    try await Task.sleep(for: .milliseconds(60))
                    try settings.capture(to: directory.appendingPathComponent("settings-\(language)-\(enabled ? "on" : "off")-\(Int(width))-bottom.png"),
                                         size: .init(width: width, height: 720))
                    settings.window?.orderOut(nil)
                }
            }
        }
        try NoticePresenter.renderFixtures(to: directory)
        try FloatingController.renderFixture(to: directory.appendingPathComponent("floating"))
        let statusProbe = AppDelegate(arguments: [])
        statusProbe.model = empty
        statusProbe.handleStatusItemClick()
        let singleShows = empty.floatingEnabled
        statusProbe.handleStatusItemClick()
        let repeatKeepsOn = empty.floatingEnabled
        empty.floatingEnabled = false
        statusProbe.handleStatusItemClick()
        let statusChecks = ["singleClickImmediatelyEnablesFloating": singleShows,
                            "repeatedClickKeepsEnabled": repeatKeepsOn,
                            "clickReenablesDisabledSetting": empty.floatingEnabled]
        try JSONSerialization.data(withJSONObject: statusChecks, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("statusbar-report.json"))
        guard !statusChecks.values.contains(false) else { throw CocoaError(.validationMultipleErrors) }
        let loginEvent = NSAppleEventDescriptor.appleEvent(withEventClass: kCoreEventClass, eventID: kAEOpenApplication,
                                                          targetDescriptor: nil, returnID: AEReturnID(kAutoGenerateReturnID),
                                                          transactionID: AETransactionID(kAnyTransactionID))
        loginEvent.setParam(NSAppleEventDescriptor(enumCode: keyAELaunchedAsLogInItem), forKeyword: keyAEPropData)
        let backgroundProbe = AppDelegate(arguments: [])
        backgroundProbe.model = empty
        backgroundProbe.controller = AppWindowController(model: empty, storesFrame: false)
        let priorPolicy = NSApp.activationPolicy()
        backgroundProbe.presentAtLaunch(inBackground: LoginStartup.startsInBackground(arguments: [], event: loginEvent))
        let hiddenAtLogin = backgroundProbe.controller?.window?.isVisible == false && NSApp.activationPolicy() == .accessory
        empty.floatingEnabled = false
        backgroundProbe.handleStatusItemClick()
        let clickShowsOnlyFloating = empty.floatingEnabled && backgroundProbe.controller?.window?.isVisible == false
        backgroundProbe.showMainWindow()
        let explicitOpenShowsWindow = backgroundProbe.controller?.window?.isVisible == true && NSApp.activationPolicy() == .regular
        backgroundProbe.controller?.window?.orderOut(nil)
        NSApp.setActivationPolicy(priorPolicy)
        let startupChecks = ["loginEventStartsWithoutMainWindow": hiddenAtLogin,
                             "backgroundClickEnablesSettingWithoutMainWindow": clickShowsOnlyFloating,
                             "explicitAppOpenShowsMainWindow": explicitOpenShowsWindow,
                             "manualLaunchIsNotLoginLaunch": !LoginStartup.startsInBackground(arguments: [], event: nil),
                             "backgroundArgumentIsRecognized": LoginStartup.startsInBackground(arguments: ["--background"], event: nil)]
        try JSONSerialization.data(withJSONObject: startupChecks, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("startup-report.json"))
        guard !startupChecks.values.contains(false) else { throw CocoaError(.validationMultipleErrors) }
        func near(_ lhs: SIMD4<Double>, _ rhs: SIMD4<Double>) -> Bool {
            (0..<4).allSatisfy { abs(lhs[$0] - rhs[$0]) < 0.000001 }
        }
        let base = SIMD4<Double>(108 / 255, 60 / 255, 224 / 255, 1)
        let dark = SIMD4<Double>(32 / 255, 38 / 255, 45 / 255, 1)
        let iconChecks = [
            "purpleMapsToInset": near(FloatingSettingsPreview.inactivePixel(base), dark),
            "whiteMapsToText2": near(FloatingSettingsPreview.inactivePixel(SIMD4(1, 1, 1, 1)), SIMD4(139 / 255, 147 / 255, 157 / 255, 1)),
            "partialAlphaKeepsPremultipliedColor": near(FloatingSettingsPreview.inactivePixel(base * 0.5), dark * 0.5),
            "transparentRemainsTransparent": near(FloatingSettingsPreview.inactivePixel(.zero), .zero),
            "blackClampsWithoutChangingAlpha": near(FloatingSettingsPreview.inactivePixel(SIMD4(0, 0, 0, 1)), SIMD4(0, 0, 0, 1)),
            "desktopPreviewFaceIs36Points": abs(FloatingSettingsPreview.paintSize(for: 40) * 54 / FloatingArtwork.canvas - 36) < 0.000001
        ]
        try JSONSerialization.data(withJSONObject: iconChecks, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("settings-icon-report.json"))
        guard !iconChecks.values.contains(false) else { throw CocoaError(.validationMultipleErrors) }
        print("화면 fixture 저장: \(directory.path); HID 및 사용자 설정 접근 없음")
    }
}
