import AppKit
import SwiftUI

@MainActor
final class AppWindowController: NSWindowController, NSWindowDelegate {
    var onClose: (() -> Void)?

    init(model: AppModel, initialPage: AppPage = .home, storesFrame: Bool = true,
         settingsExpanded: Bool = false, exportExpanded: Bool = false) {
        let window = NSWindow(contentRect: .init(x: 0, y: 0, width: 1140, height: 720),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "EasySwitch"
        window.minSize = .init(width: 860, height: 600)
        window.backgroundColor = NSColor(AppTheme.Bg)
        window.appearance = NSAppearance(named: .darkAqua)
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: AppRootView(model: model, initialPage: initialPage,
                                                               settingsExpanded: settingsExpanded, exportExpanded: exportExpanded))
        window.center()
        if storesFrame { window.setFrameAutosaveName("EasySwitchMainWindow") }
        super.init(window: window)
        window.delegate = self
    }

    required init?(coder: NSCoder) { nil }

    func windowWillClose(_ notification: Notification) { onClose?() }

    /// Fixture support for checking the folder/language rows below expanded details.
    func scrollSettingsToBottom() {
        @MainActor func visit(_ view: NSView) -> NSScrollView? {
            if let scroll = view as? NSScrollView, !(scroll.documentView is NSTextView) { return scroll }
            for child in view.subviews { if let found = visit(child) { return found } }
            return nil
        }
        guard let root = window?.contentView, let scroll = visit(root), let document = scroll.documentView else { return }
        root.layoutSubtreeIfNeeded()
        scroll.contentView.scroll(to: NSPoint(x: 0, y: max(0, document.bounds.height - scroll.contentView.bounds.height)))
        scroll.reflectScrolledClipView(scroll.contentView)
    }

    /// Captures only this app's view. It never starts HID or reads the desktop.
    func capture(to url: URL, size: NSSize = .init(width: 1140, height: 720)) throws {
        guard let window, let view = window.contentView else { throw CaptureError.missingView }
        window.setContentSize(size)
        view.layoutSubtreeIfNeeded()
        view.displayIfNeeded()
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
            throw CaptureError.bitmap
        }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]) else { throw CaptureError.bitmap }
        try png.write(to: url, options: .atomic)
    }

    private enum CaptureError: Error { case missingView, bitmap }
}
