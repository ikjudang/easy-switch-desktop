import AppKit
import SwiftUI

/// Both the button and its borderless menu use the measured English width.
struct LanguagePicker: NSViewRepresentable {
    let language: String
    let onChoose: (String) -> Void

    static var fixedWidth: CGFloat {
        let font = NSFont(name: "Pretendard-SemiBold", size: 13) ?? .systemFont(ofSize: 13, weight: .semibold)
        return ceil(("English" as NSString).size(withAttributes: [.font: font]).width + 10 + 24 + 9 + 2)
    }

    func makeNSView(context: Context) -> PickerButton { PickerButton(language: language, onChoose: onChoose) }
    func updateNSView(_ view: PickerButton, context: Context) {
        view.language = language; view.onChoose = onChoose; view.needsDisplay = true
    }

    final class PickerButton: NSButton {
        var language: String
        var onChoose: (String) -> Void
        private var menuPanel: NSPanel?
        private var localMonitor: Any?
        private var globalMonitor: Any?
        private var pendingLanguage = "ko"
        private var hovered = false
        private var tracking: NSTrackingArea?

        init(language: String, onChoose: @escaping (String) -> Void) {
            self.language = language; self.onChoose = onChoose
            super.init(frame: .zero)
            target = self; action = #selector(toggleMenu)
            isBordered = false; focusRingType = .none
            setAccessibilityLabel("언어 / Language")
        }
        required init?(coder: NSCoder) { nil }
        override var intrinsicContentSize: NSSize { .init(width: LanguagePicker.fixedWidth, height: 34) }
        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            if let tracking { removeTrackingArea(tracking) }
            let area = NSTrackingArea(rect: bounds, options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect], owner: self)
            addTrackingArea(area); tracking = area
        }
        override func mouseEntered(with event: NSEvent) { hovered = true; needsDisplay = true }
        override func mouseExited(with event: NSEvent) { hovered = false; needsDisplay = true }
        override func draw(_ dirtyRect: NSRect) {
            NSColor(AppTheme.Inset).setFill()
            let shape = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 10, yRadius: 10)
            shape.fill()
            NSColor(AppTheme.BorderStrong).setStroke(); shape.lineWidth = 1; shape.stroke()
            if hovered || isHighlighted { NSColor.white.withAlphaComponent(0.06).setFill(); shape.fill() }
            let font = NSFont(name: "Pretendard-SemiBold", size: 13) ?? .systemFont(ofSize: 13, weight: .semibold)
            let label = language == "en" ? "English" : "한국어"
            let text = NSAttributedString(string: label, attributes: [.font: font, .foregroundColor: NSColor(AppTheme.TextBody)])
            text.draw(at: NSPoint(x: 12, y: (bounds.height - text.size().height) / 2))
            let x = bounds.width - 17, y = bounds.midY
            let arrow = NSBezierPath()
            arrow.move(to: NSPoint(x: x - 3, y: y - 1.5)); arrow.line(to: NSPoint(x: x, y: y + 1.5)); arrow.line(to: NSPoint(x: x + 3, y: y - 1.5))
            NSColor(AppTheme.TextBody).setStroke(); arrow.lineWidth = 1.4; arrow.lineCapStyle = .round; arrow.stroke()
        }
        @objc private func toggleMenu() {
            guard menuPanel == nil else { dismiss(); return }
            guard let window else { return }
            let buttonRect = window.convertToScreen(convert(bounds, to: nil))
            let width = bounds.width, height: CGFloat = 88
            let screen = window.screen?.visibleFrame ?? buttonRect
            let x = min(max(buttonRect.minX, screen.minX), screen.maxX - width)
            let y = buttonRect.minY - height - 6 < screen.minY ? buttonRect.maxY + 6 : buttonRect.minY - height - 6
            let panel = NSPanel(contentRect: NSRect(x: x, y: y, width: width, height: height),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.backgroundColor = .clear; panel.isOpaque = false; panel.hasShadow = true
            panel.level = .popUpMenu; panel.appearance = NSAppearance(named: .darkAqua)
            menuPanel = panel
            pendingLanguage = language
            updateMenuContents()
            window.addChildWindow(panel, ordered: .above); panel.orderFront(nil)
            localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .keyDown]) { [weak self] event in
                let swallow = MainActor.assumeIsolated {
                    guard let self else { return false }
                    if event.type == .keyDown {
                        switch event.keyCode {
                        case 53: self.dismiss(); return true
                        case 125, 126:
                            self.pendingLanguage = event.keyCode == 125 ? "en" : "ko"
                            self.updateMenuContents(); return true
                        case 36, 76, 49:
                            let next = self.pendingLanguage
                            self.dismiss(); self.onChoose(next); return true
                        case 48: self.dismiss()
                        default: break
                        }
                    }
                    if event.type != .keyDown && event.window !== self.menuPanel {
                        // Let a second click on this button toggle the existing menu closed.
                        if event.window === self.window, self.bounds.contains(self.convert(event.locationInWindow, from: nil)) { return false }
                        self.dismiss()
                    }
                    return false
                }
                return swallow ? nil : event
            }
            globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
                Task { @MainActor in self?.dismiss() }
            }
        }
        private func updateMenuContents() {
            guard let menuPanel else { return }
            menuPanel.contentView = NSHostingView(rootView: MenuBody(language: pendingLanguage) { [weak self] next in
                self?.dismiss(); self?.onChoose(next)
            }.frame(width: menuPanel.frame.width, height: menuPanel.frame.height))
        }
        private func dismiss() {
            if let localMonitor { NSEvent.removeMonitor(localMonitor) }; localMonitor = nil
            if let globalMonitor { NSEvent.removeMonitor(globalMonitor) }; globalMonitor = nil
            if let menuPanel { menuPanel.parent?.removeChildWindow(menuPanel); menuPanel.orderOut(nil) }
            menuPanel = nil
        }
        override func viewWillMove(toWindow newWindow: NSWindow?) {
            if newWindow == nil { dismiss() }
            super.viewWillMove(toWindow: newWindow)
        }
    }

    private struct MenuBody: View {
        let language: String
        let choose: (String) -> Void
        var body: some View {
            VStack(spacing: 0) {
                option("한국어", code: "ko")
                option("English", code: "en")
            }
            .padding(4)
            .background(AppTheme.Card, in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(AppTheme.BorderStrong, lineWidth: 1))
            .preferredColorScheme(.dark)
        }
        private func option(_ title: String, code: String) -> some View {
            Button { choose(code) } label: {
                Text(title).font(AppTheme.body(13, weight: language == code ? .bold : .semibold))
                    .foregroundStyle(language == code ? AppTheme.PointAccent : AppTheme.Text)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 8).frame(height: 40)
            }
            .buttonStyle(AppActionStyle(radius: 8))
        }
    }
}
