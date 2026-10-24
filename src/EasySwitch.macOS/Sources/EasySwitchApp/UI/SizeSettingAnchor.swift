import AppKit
import SwiftUI

/// The visible size row is the sole settings exception to floating outside-click dismissal.
struct SizeSettingAnchor: NSViewRepresentable {
    let onChange: (NSRect?) -> Void
    func makeNSView(context: Context) -> AnchorView { AnchorView(onChange: onChange) }
    func updateNSView(_ view: AnchorView, context: Context) { view.onChange = onChange; view.schedulePublication() }
    static func dismantleNSView(_ view: AnchorView, coordinator: ()) { view.stop() }

    final class AnchorView: NSView {
        var onChange: (NSRect?) -> Void
        private var observers: [NSObjectProtocol] = []
        private var queued = false
        private var stopped = false
        private var lastRect: NSRect?
        init(onChange: @escaping (NSRect?) -> Void) { self.onChange = onChange; super.init(frame: .zero) }
        required init?(coder: NSCoder) { nil }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); observe(); schedulePublication() }
        override func viewDidMoveToSuperview() { super.viewDidMoveToSuperview(); observe(); schedulePublication() }
        override func layout() { super.layout(); schedulePublication() }
        override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); schedulePublication() }
        override func setFrameOrigin(_ newOrigin: NSPoint) { super.setFrameOrigin(newOrigin); schedulePublication() }

        private func observe() {
            for observer in observers { NotificationCenter.default.removeObserver(observer) }
            observers = []
            if let window {
                for name in [NSWindow.didMoveNotification, NSWindow.didResizeNotification,
                             NSWindow.didBecomeKeyNotification, NSWindow.didResignKeyNotification,
                             NSWindow.didMiniaturizeNotification, NSWindow.didDeminiaturizeNotification,
                             NSWindow.didChangeOcclusionStateNotification] {
                    observers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                        Task { @MainActor in self?.schedulePublication() }
                    })
                }
            }
            if let clip = enclosingScrollView?.contentView {
                clip.postsBoundsChangedNotifications = true
                observers.append(NotificationCenter.default.addObserver(forName: NSView.boundsDidChangeNotification, object: clip, queue: .main) { [weak self] _ in
                    Task { @MainActor in self?.schedulePublication() }
                })
            }
        }
        func schedulePublication() {
            guard !stopped, !queued else { return }
            queued = true
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.queued = false
                guard !self.stopped else { return }
                let visible = self.visibleRect.intersection(self.bounds)
                let rect: NSRect?
                if let window = self.window, window.isVisible, window.isKeyWindow,
                   !self.isHiddenOrHasHiddenAncestor, !visible.isEmpty {
                    rect = window.convertToScreen(self.convert(visible, to: nil))
                } else { rect = nil }
                if rect != self.lastRect { self.lastRect = rect; self.onChange(rect) }
            }
        }
        func stop() {
            stopped = true
            for observer in observers { NotificationCenter.default.removeObserver(observer) }
            observers = []
            lastRect = nil; onChange(nil)
        }
    }
}
