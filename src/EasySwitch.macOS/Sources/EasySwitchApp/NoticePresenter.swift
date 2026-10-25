import AppKit
import EasySwitchEngine

@MainActor
final class NoticePresenter {
    @MainActor private final class Slot {
        let panel = NoticePanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        let view = NoticeView()
        var run = -1
        init(log: FileLog?) {
            panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true
            panel.level = .statusBar; panel.ignoresMouseEvents = true
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
            panel.animationBehavior = .none; panel.isReleasedWhenClosed = false
            panel.contentView = view
            view.log = log
        }
    }
    private var switching: [String: Slot] = [:], connections: [String: Slot] = [:]
    private var rows: [NoticeRow] = [], connectionRows: [NoticeRow] = []
    private var activeRun = -1
    private var hideSwitch: Task<Void, Never>?, hideConnection: Task<Void, Never>?
    private let log: FileLog?

    init(log: FileLog? = nil) { self.log = log }

    func show(run: Int, target: Int, rows incoming: [NoticeRow], connection: Bool, english: Bool, reservedNames: [String]) {
        if connection { connectionRows = merge(connectionRows, incoming) }
        else {
            guard run >= activeRun else { return }
            if run > activeRun { rows = []; activeRun = run }
            rows = merge(rows, incoming)
        }
        let display = connection ? connectionRows : rows
        var slots = connection ? connections : switching
        let screenKeys = Set(NSScreen.screens.map { String(describing: $0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] ?? 0) })
        for removed in slots.keys where !screenKeys.contains(removed) { slots[removed]?.panel.close(); slots[removed] = nil }
        for screen in NSScreen.screens {
            let key = String(describing: screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] ?? 0)
            let slot = slots[key] ?? Slot(log: log); slots[key] = slot
            slot.view.run = run
            let fresh = slot.run != run || slot.view.english != english || slot.view.connection != connection || !slot.panel.isVisible
            let oldSize = slot.panel.frame.size
            let size = slot.view.update(target: target, rows: display, connection: connection, english: english,
                                        reservedNames: reservedNames, maximumWidth: screen.visibleFrame.width - 48, fresh: fresh)
            if fresh || oldSize != size || !slot.panel.isVisible {
                let area = screen.visibleFrame
                let x = fresh ? area.midX - size.width / 2 : slot.panel.frame.minX
                let y: CGFloat
                if connection, let below = switching[key], below.panel.isVisible { y = below.panel.frame.maxY + 12 }
                else if fresh || !slot.panel.isVisible { y = area.minY + 64 }
                else { y = slot.panel.frame.maxY - size.height }
                slot.panel.setFrame(.init(x: x, y: y, width: size.width, height: size.height), display: false)
            }
            slot.run = run; slot.view.needsDisplay = true; slot.panel.orderFrontRegardless()
            // Present the initial state before a fast HID switch can finish on another thread.
            slot.panel.displayIfNeeded(); slot.view.displayIfNeeded()
            if fresh { log?.write("TOAST_SHOW run=\(run) connection=\(connection) visible=\(slot.panel.isVisible) screen=\(key)") }
        }
        if connection { connections = slots; hideConnection?.cancel(); hideConnection = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(2)) } catch { return }
            self?.connectionRows = []; for slot in self?.connections.values ?? [:].values { slot.panel.orderOut(nil) }
            self?.log?.write("TOAST_HIDE run=\(run) connection=true")
        } }
        else {
            switching = slots
            for (key, connectionSlot) in connections where connectionSlot.panel.isVisible {
                if let below = switching[key] {
                    connectionSlot.panel.setFrameOrigin(.init(x: connectionSlot.panel.frame.minX, y: below.panel.frame.maxY + 12))
                }
            }
            hideSwitch?.cancel(); hideSwitch = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(2)) } catch { return }
            for slot in self?.switching.values ?? [:].values { slot.panel.orderOut(nil) }
            self?.log?.write("TOAST_HIDE run=\(run) connection=false")
        } }
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
        for slot in Array(switching.values) + Array(connections.values) { slot.panel.close() }
        switching = [:]; connections = [:]
    }

    static func renderFixtures(to directory: URL) throws {
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
                let view = NoticeView()
                let size = view.update(target: 1, rows: rows, connection: connection, english: english,
                                       reservedNames: rows.map(\.name), maximumWidth: maximumWidth, fresh: true)
                view.frame = .init(origin: .zero, size: size)
                guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { continue }
                view.cacheDisplay(in: view.bounds, to: bitmap)
                let suffix = maximumWidth == 600 ? "" : "-constrained"
                try bitmap.representation(using: .png, properties: [:])?.write(to: directory.appendingPathComponent(name + suffix + ".png"))
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
        let checks = ["statusChangesKeepFrame": statusStable, "lateRowGrowsBelowFixedHeader": addedBelow,
                      "sameRequestRetainsSurface": retained, "connectionNoticeAboveSwitch": stacked,
                      "fastSwitchStartDrawsBeforeCompletion": fastStartDrawn, "fastSwitchCompletionDrawsWhileVisible": fastCompletionDrawn]
        presenter.stop()
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
    var log: FileLog?
    private(set) var renderedStates: [NoticeState] = []
    private var lastDrawn = ""
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
            let signature = "run=\(run) connection=\(connection) states=\(renderedStates.map(\.rawValue).joined(separator: ","))"
            if signature != lastDrawn { lastDrawn = signature; log?.write("TOAST_DRAW \(signature)") }
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
