import AppKit
import SwiftUI

/// Windows reserves 8 points of separation plus a 24-point scroller control.
/// Explicit tiling keeps that 32-point gutter independent of OS scroller settings.
final class StableGutterScrollView: NSScrollView {
    static let gutter: CGFloat = 32
    static let controlWidth: CGFloat = 24
    private let contentPadding: CGFloat
    private let scrollerVerticalPadding: CGFloat

    init(contentPadding: CGFloat = 0, scrollerVerticalPadding: CGFloat = 0) {
        self.contentPadding = contentPadding
        self.scrollerVerticalPadding = scrollerVerticalPadding
        super.init(frame: .zero)
        borderType = .noBorder
        drawsBackground = false
        contentView.drawsBackground = false
        automaticallyAdjustsContentInsets = false
        hasVerticalScroller = true
        hasHorizontalScroller = false
        autohidesScrollers = false
        scrollerStyle = .legacy
        verticalScroller = RoundedScroller()
    }
    required init?(coder: NSCoder) { nil }

    override func tile() {
        super.tile()
        // An inset of a not-yet-sized view can produce CGRect.null/infinite origins.
        let padX = min(contentPadding, max(0, bounds.width / 2))
        let padY = min(contentPadding, max(0, bounds.height / 2))
        let area = NSRect(x: bounds.minX + padX, y: bounds.minY + padY,
                          width: max(0, bounds.width - padX * 2), height: max(0, bounds.height - padY * 2))
        contentView.frame = NSRect(x: area.minX, y: area.minY,
                                   width: max(0, area.width - Self.gutter), height: max(0, area.height))
        verticalScroller?.frame = NSRect(x: max(area.minX, area.maxX - Self.controlWidth),
                                        y: area.minY + scrollerVerticalPadding,
                                        width: Self.controlWidth,
                                        height: max(0, area.height - scrollerVerticalPadding * 2))
    }

    override var isOpaque: Bool { drawsBackground }
    override func draw(_ dirtyRect: NSRect) {
        // Calling the native drawing path can paint a legacy separator or track.
        if drawsBackground { backgroundColor.setFill(); NSBezierPath(rect: dirtyRect).fill() }
    }
}

/// The gutter remains reserved when the document is too short to show a thumb.
struct StableScroll<Content: View>: NSViewRepresentable {
    @ViewBuilder var content: Content

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = StableGutterScrollView()
        let host = NSHostingView(rootView: AnyView(content))
        host.translatesAutoresizingMaskIntoConstraints = false
        scroll.documentView = host
        NSLayoutConstraint.activate([
            host.leadingAnchor.constraint(equalTo: scroll.contentView.leadingAnchor),
            host.topAnchor.constraint(equalTo: scroll.contentView.topAnchor),
            host.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor)
        ])
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        (scroll.documentView as? NSHostingView<AnyView>)?.rootView = AnyView(content)
    }
}

final class RoundedScroller: NSScroller {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
    }
    required init?(coder: NSCoder) { nil }

    override var isOpaque: Bool { false }
    override func setNeedsDisplay(_ invalidRect: NSRect) {
        // Native knob invalidation uses narrower metrics than our centered thumb.
        // Redraw both its old and new positions, including the transparent margins.
        super.setNeedsDisplay(bounds)
    }
    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.cgContext.clear(bounds)
        drawKnob()
    }
    override func drawKnobSlot(in slotRect: NSRect, highlight flag: Bool) {}
    override func drawKnob() {
        guard isEnabled, knobProportion > 0, knobProportion < 1 else { return }
        let native = rect(for: .knob)
        let thumb = NSRect(x: bounds.midX - 6, y: native.minY, width: 12, height: native.height)
        guard thumb.height >= 12 else { return }
        NSColor(AppTheme.BorderStrong).withAlphaComponent(0.6).setFill()
        NSBezierPath(roundedRect: thumb, xRadius: 6, yRadius: 6).fill()
    }
}

struct FinePercentSlider: View {
    @Binding var value: Int
    let bounds: ClosedRange<Int>
    let label: String
    @State private var lastX: CGFloat?
    @State private var preciseValue: Double?
    @State private var hovered = false

    var body: some View {
        GeometryReader { geometry in
            let track = max(1, geometry.size.width - 40)
            let range = Double(bounds.upperBound - bounds.lowerBound)
            let x = 20 + track * Double(value - bounds.lowerBound) / range
            Canvas { context, size in
                context.fill(Path(roundedRect: .init(x: 20, y: 22, width: track, height: 4), cornerRadius: 2), with: .color(AppTheme.BorderStrong))
                context.fill(Path(roundedRect: .init(x: 20, y: 22, width: max(0, x - 20), height: 4), cornerRadius: 2), with: .color(AppTheme.PointTintText))
                if hovered {
                    context.fill(Path(ellipseIn: .init(x: x - 16, y: 8, width: 32, height: 32)), with: .color(AppTheme.PointTintText.opacity(0.18)))
                }
                context.fill(Path(ellipseIn: .init(x: x - 8, y: 16, width: 16, height: 16)), with: .color(AppTheme.PointTintText))
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { drag in
                    let next: Double
                    if let lastX { next = (preciseValue ?? Double(value)) + (drag.location.x - lastX) / (track * 1.5) * range }
                    else if abs(drag.location.x - x) > 16 { next = Double(bounds.lowerBound) + (drag.location.x - 20) / track * range }
                    else { next = Double(value) }
                    value = min(bounds.upperBound, max(bounds.lowerBound, Int(next.rounded())))
                    preciseValue = min(Double(bounds.upperBound), max(Double(bounds.lowerBound), next))
                    lastX = drag.location.x
                }
                .onEnded { _ in lastX = nil; preciseValue = nil })
            .onContinuousHover { phase in
                if case .active(let point) = phase { hovered = lastX == nil && abs(point.x - x) <= 16 }
                else { hovered = false }
            }
        }
        .frame(minWidth: 40).frame(height: 48)
        .focusable()
        .onMoveCommand { direction in
            if direction == .left { value = max(bounds.lowerBound, value - 1) }
            if direction == .right { value = min(bounds.upperBound, value + 1) }
        }
        .accessibilityElement()
        .accessibilityLabel(label)
        .accessibilityValue("\(value)%")
        .accessibilityAdjustableAction { direction in
            value = min(bounds.upperBound, max(bounds.lowerBound, value + (direction == .increment ? 1 : -1)))
        }
    }
}

struct PercentSettingRow: View {
    let title: String
    let labelWidth: CGFloat
    @Binding var value: Int
    let bounds: ClosedRange<Int>
    @State private var draft = ""
    @FocusState private var editing: Bool

    var body: some View {
        HStack(spacing: 0) {
            Text(title).font(AppTheme.body(13, weight: .bold)).lineLimit(1)
                .frame(width: labelWidth, alignment: .leading)
            HStack(alignment: .firstTextBaseline, spacing: 2) {
                TextField("", text: $draft)
                    .textFieldStyle(.plain).multilineTextAlignment(.trailing)
                    .font(AppTheme.body(13)).frame(width: 27)
                    .focused($editing).onSubmit { commit(); editing = false }
                    .accessibilityLabel(title)
                Text("%").font(AppTheme.body(12)).foregroundStyle(AppTheme.Text2)
            }
            .padding(.horizontal, 8).frame(height: 28)
            .background(AppTheme.Inset, in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(AppTheme.BorderStrong, lineWidth: 1))
            .padding(.leading, 12)
            FinePercentSlider(value: $value, bounds: bounds, label: title)
        }
        .padding(.leading, 22.5).padding(.trailing, 6).frame(height: 70)
        .onAppear { draft = String(value) }
        .onChange(of: value) { if !editing { draft = String($0) } }
        .onChange(of: editing) { if !$0 { commit() } }
        .onChange(of: draft) { text in
            guard editing, !text.isEmpty else { return }
            guard text.allSatisfy(\.isNumber), let number = Int(text) else { draft = String(value); return }
            let clamped = min(bounds.upperBound, max(0, number))
            if text != String(clamped) { draft = String(clamped) }
            if bounds.contains(clamped) { value = clamped }
        }
    }

    private func commit() {
        let next = min(bounds.upperBound, max(bounds.lowerBound, Int(draft) ?? value))
        if next != value { value = next }
        draft = String(value)
    }
}

/// Captures only a secondary click; ordinary clicks continue to the channel button.
struct SecondaryClickCapture: NSViewRepresentable {
    let action: () -> Void
    func makeNSView(context: Context) -> CaptureView { CaptureView(action: action) }
    func updateNSView(_ view: CaptureView, context: Context) { view.action = action }

    final class CaptureView: NSView {
        var action: () -> Void
        init(action: @escaping () -> Void) { self.action = action; super.init(frame: .zero) }
        required init?(coder: NSCoder) { nil }
        override func hitTest(_ point: NSPoint) -> NSView? {
            guard super.hitTest(point) != nil, let event = NSApp.currentEvent else { return nil }
            return event.type == .rightMouseDown || event.type == .rightMouseUp ||
                (event.type == .leftMouseDown && event.modifierFlags.contains(.control)) ? self : nil
        }
        override func rightMouseDown(with event: NSEvent) { action() }
        override func mouseDown(with event: NSEvent) { if event.modifierFlags.contains(.control) { action() } }
    }
}
