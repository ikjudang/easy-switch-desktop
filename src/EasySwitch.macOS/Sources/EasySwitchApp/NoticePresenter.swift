import AppKit
import EasySwitchEngine

@MainActor
final class NoticePresenter {
    private struct Content {
        let run: Int, target: Int
        let english: Bool
        let reservedNames: [String]
    }
    @MainActor private final class Slot {
        let panel = NoticePanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        let view = NoticeView()
        var run = -1
        init() {
            panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true
            panel.level = .statusBar; panel.ignoresMouseEvents = true
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
            panel.animationBehavior = .none; panel.isReleasedWhenClosed = false
            panel.contentView = view
        }
    }
    private var switching: [String: Slot] = [:], connections: [String: Slot] = [:]
    private var rows: [NoticeRow] = [], connectionRows: [NoticeRow] = []
    private var activeRun = -1
    private var suppressedRun = -1
    private var preferences: ToastPreferences
    private var activeContent: Content?, connectionContent: Content?
    private var hideSwitch: Task<Void, Never>?, hideConnection: Task<Void, Never>?

    init(preferences: ToastPreferences = ToastPreferences()) {
        self.preferences = preferences
    }

    func configure(_ preferences: ToastPreferences) {
        guard preferences != self.preferences else { return }
        let resized = preferences.size != self.preferences.size
        self.preferences = preferences
        if !preferences.enabled {
            suppressedRun = max(suppressedRun, activeRun)
            hideSwitch?.cancel(); hideConnection?.cancel()
            hideSwitch = nil; hideConnection = nil
            activeContent = nil; connectionContent = nil; rows = []; connectionRows = []
            releasePanels()
            return
        }
        if let content = activeContent { present(content, connection: false, relayout: resized) }
        if let content = connectionContent { present(content, connection: true, relayout: resized) }
    }

    func show(run: Int, target: Int, rows incoming: [NoticeRow], connection: Bool, english: Bool, reservedNames: [String]) {
        guard preferences.enabled else {
            if !connection { suppressedRun = max(suppressedRun, run) }
            return
        }
        guard connection || run > suppressedRun else {
            return
        }
        if connection { connectionRows = merge(connectionRows, incoming) }
        else {
            guard run >= activeRun else { return }
            if run > activeRun { rows = []; activeRun = run }
            rows = merge(rows, incoming)
        }
        let content = Content(run: run, target: target, english: english, reservedNames: reservedNames)
        if connection { connectionContent = content }
        else { activeContent = content }
        present(content, connection: connection)
        if connection {
            hideConnection?.cancel(); hideConnection = Task { [weak self] in
                do { try await Task.sleep(for: .seconds(2)) } catch { return }
                self?.connectionContent = nil; self?.connectionRows = []
                for slot in self?.connections.values ?? [:].values { slot.panel.orderOut(nil) }
            }
        } else {
            hideSwitch?.cancel(); hideSwitch = Task { [weak self] in
                do { try await Task.sleep(for: .seconds(2)) } catch { return }
                self?.activeContent = nil
                for slot in self?.switching.values ?? [:].values { slot.panel.orderOut(nil) }
            }
        }
    }

    private func present(_ content: Content, connection: Bool, relayout: Bool = false) {
        let run = content.run, scale = preferences.scale
        let display = connection ? connectionRows : rows
        var slots = connection ? connections : switching
        let screenKeys = Set(NSScreen.screens.map { String(describing: $0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] ?? 0) })
        for removed in slots.keys where !screenKeys.contains(removed) { slots[removed]?.panel.close(); slots[removed] = nil }
        for screen in NSScreen.screens {
            let key = String(describing: screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] ?? 0)
            let slot = slots[key] ?? Slot(); slots[key] = slot
            slot.view.run = run
            let fresh = relayout || slot.run != run || slot.view.english != content.english || slot.view.connection != connection || !slot.panel.isVisible
            let oldSize = slot.panel.frame.size
            let logicalSize = slot.view.update(target: content.target, rows: display, connection: connection, english: content.english,
                                               reservedNames: content.reservedNames, maximumWidth: (screen.visibleFrame.width - 48) / scale, fresh: fresh)
            let size = NSSize(width: logicalSize.width * scale, height: logicalSize.height * scale)
            if fresh || oldSize != size || !slot.panel.isVisible {
                let area = screen.visibleFrame
                let x = fresh ? area.midX - size.width / 2 : slot.panel.frame.minX
                let y: CGFloat
                if connection, let below = switching[key], below.panel.isVisible { y = below.panel.frame.maxY + 12 * scale }
                else if fresh || !slot.panel.isVisible { y = area.minY + 64 }
                else { y = slot.panel.frame.maxY - size.height }
                slot.panel.setFrame(.init(x: x, y: y, width: size.width, height: size.height), display: false)
            }
            slot.view.frame = .init(origin: .zero, size: size)
            slot.view.setBoundsSize(logicalSize)
            slot.panel.alphaValue = preferences.opacity
            slot.run = run; slot.view.needsDisplay = true; slot.panel.orderFrontRegardless()
            // Present the initial state before a fast HID switch can finish on another thread.
            slot.panel.displayIfNeeded(); slot.view.displayIfNeeded()
        }
        if connection { connections = slots }
        else {
            switching = slots
            for (key, connectionSlot) in connections where connectionSlot.panel.isVisible {
                if let below = switching[key] {
                    connectionSlot.panel.setFrameOrigin(.init(x: connectionSlot.panel.frame.minX, y: below.panel.frame.maxY + 12 * scale))
                }
            }
        }
    }

    private func merge(_ previous: [NoticeRow], _ incoming: [NoticeRow]) -> [NoticeRow] {
        var result = previous
        for row in incoming {
            if let index = result.firstIndex(where: { $0.id == row.id }) {
                if result[index].state == .switching || row.state != .switching { result[index] = row }
            } else { result.append(row) }
        }
        return result.sorted { ($0.kind == "키보드" ? 0 : 1) < ($1.kind == "키보드" ? 0 : 1) }
    }
    func stop() {
        hideSwitch?.cancel(); hideConnection?.cancel()
        hideSwitch = nil; hideConnection = nil
        activeContent = nil; connectionContent = nil; rows = []; connectionRows = []
        releasePanels()
    }

    private func releasePanels() {
        for slot in Array(switching.values) + Array(connections.values) {
            slot.panel.contentView = nil; slot.panel.close()
        }
        switching.removeAll(); connections.removeAll()
    }

    static func renderFixtures(to directory: URL) async throws {
        let cases: [(String, Bool, Bool, [NoticeRow])] = [
            ("toast-command-ko", false, false, [NoticeRow(id: "k", name: "MX MCHNCL", kind: "키보드", state: .complete), NoticeRow(id: "m", name: "MX Master 4", kind: "마우스", state: .switching)]),
            ("toast-command-en", true, false, [NoticeRow(id: "k", name: "MX MCHNCL", kind: "키보드", state: .complete), NoticeRow(id: "m", name: "MX Master 4", kind: "마우스", state: .unconfirmed)]),
            ("toast-connected", false, true, [NoticeRow(id: "m", name: "MX Master 4", kind: "마우스", state: .connected)]),
            ("toast-connected-en", true, true, [NoticeRow(id: "m", name: "MX Master 4", kind: "마우스", state: .connected)]),
            ("toast-connected-multi", false, true, [NoticeRow(id: "k", name: "MX MCHNCL", kind: "키보드", state: .connected), NoticeRow(id: "m", name: "MX Master 4", kind: "마우스", state: .connected)]),
            ("toast-complete-ko", false, false, [NoticeRow(id: "k", name: "MX KEYS S", kind: "키보드", state: .complete), NoticeRow(id: "m", name: "MX Master 3S", kind: "마우스", state: .complete)]),
            ("toast-complete-en", true, false, [NoticeRow(id: "k", name: "MX KEYS S", kind: "키보드", state: .complete), NoticeRow(id: "m", name: "MX Master 3S", kind: "마우스", state: .complete)]),
            ("toast-switching-en", true, false, [NoticeRow(id: "k", name: "MX KEYS S", kind: "키보드", state: .complete), NoticeRow(id: "m", name: "MX Master 3S", kind: "마우스", state: .switching)]),
            ("toast-long-name-ko", false, false, [NoticeRow(id: "k", name: "MX Mechanical Keyboard", kind: "키보드", state: .unconfirmed), NoticeRow(id: "m", name: "MX Anywhere 3S Mouse", kind: "마우스", state: .failed)]),
            ("toast-long-name-en", true, false, [NoticeRow(id: "k", name: "MX Mechanical Keyboard", kind: "키보드", state: .unconfirmed), NoticeRow(id: "m", name: "MX Anywhere 3S Mouse", kind: "마우스", state: .failed)]),
            ("toast-general-ko", false, false, []),
            ("toast-general-en", true, false, [])
        ]
        for (name, english, connection, rows) in cases {
            for maximumWidth in [CGFloat(600), 300] {
                for percent in [50, 100, 200] {
                    let scale = CGFloat(percent) / 100
                    let view = NoticeView()
                    let size = view.update(target: 1, rows: rows, connection: connection, english: english,
                                           reservedNames: rows.map(\.name), maximumWidth: maximumWidth / scale, fresh: true)
                    view.frame = .init(x: 0, y: 0, width: size.width * scale, height: size.height * scale)
                    view.setBoundsSize(size)
                    guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { continue }
                    view.cacheDisplay(in: view.bounds, to: bitmap)
                    let suffix = (maximumWidth == 600 ? "" : "-constrained") + (percent == 100 ? "" : "-\(percent)")
                    try bitmap.representation(using: .png, properties: [:])?.write(to: directory.appendingPathComponent(name + suffix + ".png"))
                }
            }
        }
        let presenter = NoticePresenter()
        let keyboard = NoticeRow(id: "fixture-k", name: "MX MCHNCL", kind: "키보드", state: .switching)
        let mouse = NoticeRow(id: "fixture-m", name: "MX Master 4", kind: "마우스", state: .switching)
        presenter.show(run: 10, target: 1, rows: [keyboard], connection: false, english: false, reservedNames: [keyboard.name, mouse.name])
        let initial = presenter.switching.mapValues { ($0.panel.frame, ObjectIdentifier($0.view)) }
        presenter.show(run: 10, target: 1, rows: [NoticeRow(id: keyboard.id, name: keyboard.name, kind: keyboard.kind, state: .complete)],
                       connection: false, english: false, reservedNames: [keyboard.name, mouse.name])
        let statusStable = presenter.switching.allSatisfy { initial[$0.key]?.0 == $0.value.panel.frame }
        presenter.show(run: 10, target: 1, rows: [mouse], connection: false, english: false, reservedNames: [keyboard.name, mouse.name])
        let addedBelow = presenter.switching.allSatisfy {
            guard let before = initial[$0.key]?.0 else { return false }
            let after = $0.value.panel.frame
            return after.minX == before.minX && after.width == before.width && after.maxY == before.maxY && after.height > before.height
        }
        let retained = presenter.switching.allSatisfy { initial[$0.key]?.1 == ObjectIdentifier($0.value.view) }
        presenter.show(run: 0, target: 0, rows: [NoticeRow(id: keyboard.id, name: keyboard.name, kind: keyboard.kind, state: .connected)],
                       connection: true, english: false, reservedNames: [keyboard.name, mouse.name])
        let stacked = presenter.connections.allSatisfy {
            guard let below = presenter.switching[$0.key] else { return false }
            return abs($0.value.panel.frame.minY - below.panel.frame.maxY - 12) < 0.5
        }
        presenter.show(run: 11, target: 1, rows: [keyboard, mouse], connection: false, english: false,
                       reservedNames: [keyboard.name, mouse.name])
        let fastStartDrawn = presenter.switching.values.allSatisfy { $0.panel.isVisible && $0.view.renderedStates == [.switching, .switching] }
        let complete = [keyboard, mouse].map { NoticeRow(id: $0.id, name: $0.name, kind: $0.kind, state: .complete) }
        presenter.show(run: 11, target: 1, rows: complete, connection: false, english: false,
                       reservedNames: [keyboard.name, mouse.name])
        let fastCompletionDrawn = presenter.switching.values.allSatisfy { $0.panel.isVisible && $0.view.renderedStates == [.complete, .complete] }
        var checks = ["statusChangesKeepFrame": statusStable, "lateRowGrowsBelowFixedHeader": addedBelow,
                      "sameRequestRetainsSurface": retained, "connectionNoticeAboveSwitch": stacked,
                      "fastSwitchStartDrawsBeforeCompletion": fastStartDrawn, "fastSwitchCompletionDrawsWhileVisible": fastCompletionDrawn]

        let normal = presenter.switching.mapValues { $0.panel.frame }
        presenter.configure(ToastPreferences(size: 50))
        checks["liveHalfSizeKeepsBottomAnchor"] = !normal.isEmpty && presenter.switching.allSatisfy {
            guard let before = normal[$0.key] else { return false }
            let after = $0.value.panel.frame
            return abs(after.width - before.width / 2) < 1 && abs(after.height - before.height / 2) < 1 && abs(after.minY - before.minY) < 1
        }
        presenter.configure(ToastPreferences(size: 200))
        checks["liveDoubleSizeKeepsBottomAnchor"] = presenter.switching.allSatisfy {
            guard let before = normal[$0.key] else { return false }
            let after = $0.value.panel.frame
            return abs(after.width - before.width * 2) < 1 && abs(after.height - before.height * 2) < 1 && abs(after.minY - before.minY) < 1
        }
        checks["scaledConnectionStacking"] = presenter.connections.allSatisfy {
            guard let below = presenter.switching[$0.key] else { return false }
            return abs($0.value.panel.frame.minY - below.panel.frame.maxY - 24) < 0.5
        }
        let largeFrames = presenter.switching.mapValues { $0.panel.frame }
        presenter.configure(ToastPreferences(size: 200, transparency: 75))
        checks["liveOpacityKeepsFramesAndSurfaces"] = presenter.switching.allSatisfy {
            $0.value.panel.alphaValue == 0.25 && $0.value.panel.frame == largeFrames[$0.key] && initial[$0.key]?.1 == ObjectIdentifier($0.value.view)
        } && presenter.connections.values.allSatisfy { $0.panel.alphaValue == 0.25 }
        presenter.configure(ToastPreferences(transparency: 100))
        checks["fullyTransparentToasts"] = (Array(presenter.switching.values) + Array(presenter.connections.values)).allSatisfy { $0.panel.alphaValue == 0 }
        presenter.configure(ToastPreferences(transparency: 30))
        checks["opacityCanBeRestored"] = presenter.switching.values.allSatisfy { $0.panel.isVisible && abs($0.panel.alphaValue - 0.7) < 0.00001 }
        presenter.configure(ToastPreferences(enabled: false))
        checks["disableHidesBothKinds"] = (Array(presenter.switching.values) + Array(presenter.connections.values)).allSatisfy { !$0.panel.isVisible }
        checks["disableReleasesBothKindsOfWindows"] = presenter.switching.isEmpty && presenter.connections.isEmpty && presenter.hideSwitch == nil && presenter.hideConnection == nil
        presenter.show(run: 12, target: 1, rows: [keyboard], connection: false, english: false, reservedNames: [])
        presenter.show(run: 0, target: 0, rows: [mouse], connection: true, english: false, reservedNames: [])
        presenter.configure(ToastPreferences())
        presenter.show(run: 12, target: 1, rows: complete, connection: false, english: false, reservedNames: [])
        checks["reenableDoesNotReplayOldNotifications"] = presenter.activeContent == nil && presenter.connectionContent == nil &&
            (Array(presenter.switching.values) + Array(presenter.connections.values)).allSatisfy { !$0.panel.isVisible }
        presenter.show(run: 13, target: 1, rows: [keyboard], connection: false, english: false, reservedNames: [])
        checks["newRunShowsAfterReenable"] = presenter.switching.values.allSatisfy { $0.panel.isVisible && $0.view.renderedStates == [.switching] }
        try await Task.sleep(for: .milliseconds(2100))
        presenter.configure(ToastPreferences(size: 150, transparency: 20))
        checks["settingsDoNotReviveExpiredToasts"] = presenter.activeContent == nil && presenter.switching.values.allSatisfy { !$0.panel.isVisible }
        presenter.stop()

        let disabled = NoticePresenter(preferences: ToastPreferences(enabled: false))
        disabled.show(run: 1, target: 1, rows: [keyboard], connection: false, english: false, reservedNames: [])
        disabled.show(run: 0, target: 0, rows: [mouse], connection: true, english: false, reservedNames: [])
        disabled.configure(ToastPreferences(size: 70, transparency: 60))
        disabled.show(run: 1, target: 1, rows: complete, connection: false, english: false, reservedNames: [])
        checks["disabledStartupCreatesNoPanelsOrReplay"] = disabled.switching.isEmpty && disabled.connections.isEmpty
        disabled.show(run: 2, target: 1, rows: [keyboard], connection: false, english: false, reservedNames: [])
        let presentationDetails = disabled.switching.mapValues {
            ["frame": NSStringFromRect($0.panel.frame), "viewFrame": NSStringFromRect($0.view.frame),
             "bounds": NSStringFromRect($0.view.bounds), "opacity": String(describing: $0.panel.alphaValue),
             "visible": String($0.panel.isVisible)]
        }
        try JSONSerialization.data(withJSONObject: presentationDetails, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("toast-presentation-details.json"))
        checks["disabledStartupRetainsSizeAndOpacity"] = !disabled.switching.isEmpty && disabled.switching.values.allSatisfy {
            // AppKit rounds the outer panel up to whole points at fractional sizes.
            $0.panel.isVisible && abs($0.panel.alphaValue - 0.4) < 0.00001 &&
                abs($0.panel.frame.width - $0.view.bounds.width * 0.7) <= 1 && abs($0.panel.frame.height - $0.view.bounds.height * 0.7) <= 1
        }
        disabled.stop()

        let suite = "EasySwitch.ToastFixture.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(126, forKey: "floatingSize")
        checks["legacyPreferencesKeepToastAppearance"] = ToastPreferences(defaults: defaults) == ToastPreferences()
        let values = [ToastPreferences(enabled: false, size: -1, transparency: -1), ToastPreferences(size: 50),
                      ToastPreferences(size: 75, transparency: 40), ToastPreferences(size: 200, transparency: 100),
                      ToastPreferences(size: 999, transparency: 999)]
        checks["preferencesClampAndPersistIndependently"] = values.allSatisfy {
            $0.save(to: defaults)
            return ToastPreferences(defaults: UserDefaults(suiteName: suite)!) == $0 && defaults.integer(forKey: "floatingSize") == 126
        } && values.first?.size == 50 && values.first?.transparency == 0 && values.last?.size == 200 && values.last?.transparency == 100
        let report = try JSONSerialization.data(withJSONObject: checks, options: [.prettyPrinted, .sortedKeys])
        try report.write(to: directory.appendingPathComponent("toast-report.json"))
        if checks.values.contains(false) { throw CocoaError(.validationMultipleErrors) }
    }
}

private final class NoticePanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
private final class NoticeView: NSView {
    override var isFlipped: Bool { true }
    var english = false, connection = false
    var run = -1
    private(set) var renderedStates: [NoticeState] = []
    private var rows: [NoticeRow] = []
    private var title = ""
    private var contentWidth: CGFloat = 180, statusWidth: CGFloat = 80
    private var bodyHeight: CGFloat = 72
    private let font = NSFont(name: "Pretendard-Regular", size: 21) ?? .systemFont(ofSize: 21)
    private let icon = AppIcon.makeImage(size: 72)

    func update(target: Int, rows: [NoticeRow], connection: Bool, english: Bool, reservedNames: [String], maximumWidth: CGFloat, fresh: Bool) -> NSSize {
        self.english = english; self.connection = connection; self.rows = rows
        title = english ? "Switch to channel \(target + 1)" : "채널 \(target + 1) 전환"
        if fresh {
            let statuses = english ? ["Complete", "Failed", "Switching", "Unconfirmed", "Connected"] : ["완료", "실패", "전환 중", "확인 필요", "연결됨"]
            statusWidth = ceil(statuses.map(width).max() ?? 60)
            if rows.isEmpty {
                let names = reservedNames.map(width).max() ?? 80
                contentWidth = min(max(1, min(450, maximumWidth) - 87), max(180, max(width(title), names + 12 + statusWidth)))
            } else {
                let nameWidth = min(150, max(1, min(450, maximumWidth) - 87 - 12 - statusWidth))
                contentWidth = nameWidth + 12 + statusWidth
            }
        }
        bodyHeight = rows.isEmpty ? 36 + 36 + 30 : 36 + max(36, (connection ? 0 : 36) + 27 * CGFloat(rows.count))
        return .init(width: contentWidth + 111, height: bodyHeight + 24)
    }

    override func draw(_ dirtyRect: NSRect) {
        defer {
            renderedStates = rows.map(\.state)
        }
        let body = bounds.insetBy(dx: 12, dy: 12)
        NSColor(srgbRed: 48/255, green: 48/255, blue: 48/255, alpha: 240/255).setFill()
        NSBezierPath(roundedRect: body, xRadius: 36, yRadius: 36).fill()
        let iconRect = NSRect(x: 30, y: body.midY - 18, width: 36, height: 36)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(roundedRect: iconRect, xRadius: 13.5, yRadius: 13.5).addClip()
        icon.draw(in: iconRect)
        NSGraphicsContext.restoreGraphicsState()
        var y: CGFloat = 30
        let x: CGFloat = 81
        if !connection { drawText(title, at: .init(x: x, y: y), width: contentWidth, height: 36); y += 36 }
        if rows.isEmpty { drawText(english ? "No devices to switch" : "전환할 기기가 없음", at: .init(x: x, y: y), width: contentWidth, height: 30); return }
        if connection { y += max(0, (bodyHeight - 36 - 27 * CGFloat(rows.count)) / 2) }
        for row in rows {
            drawText(row.name, at: .init(x: x, y: y), width: max(1, contentWidth - statusWidth - 12))
            let status = status(row.state)
            drawText(status.0, at: .init(x: x + contentWidth - statusWidth, y: y), width: statusWidth, color: status.1)
            y += 27
        }
    }
    private func status(_ state: NoticeState) -> (String, NSColor) {
        switch state {
        case .complete: (english ? "Complete" : "완료", NSColor(srgbRed: 0, green: 230/255, blue: 118/255, alpha: 1))
        case .failed: (english ? "Failed" : "실패", NSColor(srgbRed: 229/255, green: 57/255, blue: 53/255, alpha: 1))
        case .unconfirmed: (english ? "Unconfirmed" : "확인 필요", .white)
        case .connected: (english ? "Connected" : "연결됨", .white)
        case .switching: (english ? "Switching" : "전환 중", .white)
        }
    }
    private func width(_ string: String) -> CGFloat { (string as NSString).size(withAttributes: [.font: font]).width }
    private func drawText(_ string: String, at point: NSPoint, width: CGFloat, height: CGFloat = 27, color: NSColor = .white) {
        let paragraph = NSMutableParagraphStyle(); paragraph.lineBreakMode = .byTruncatingTail
        paragraph.alignment = .left
        let textHeight = (string as NSString).size(withAttributes: [.font: font]).height
        (string as NSString).draw(in: .init(x: point.x, y: point.y + (height - textHeight) / 2, width: width, height: max(28, textHeight)),
                                 withAttributes: [.font: font, .foregroundColor: color, .paragraphStyle: paragraph])
    }
}
