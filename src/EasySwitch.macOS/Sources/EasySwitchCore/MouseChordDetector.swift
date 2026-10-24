import Foundation

/// Passive per-device recognition in raw physical pixels. The native host supplies one stable ID per mouse/display.
public final class MouseChordDetector {
    public struct Sample: Sendable {
        public let device: Int64, display: Int64, at: Int64
        public let buttons: Int
        public let x: Double, y: Double
        public let cancelled: Bool
        public init(device: Int64, display: Int64, at: Int64, buttons: Int, x: Double, y: Double, cancelled: Bool = false) {
            self.device = device; self.display = display; self.at = at; self.buttons = buttons
            self.x = x; self.y = y; self.cancelled = cancelled
        }
    }
    private struct Press {
        let display: Int64, started: Int64, x: Double, y: Double
        var last: Int64, buttons = 0, armed = false, rejected = false
        init(_ sample: Sample) {
            display = sample.display; started = sample.at; last = sample.at; x = sample.x; y = sample.y
        }
    }
    private var presses: [Int64: Press] = [:]
    public init() {}
    public func reset() { presses.removeAll() }
    public func remove(_ device: Int64) { presses[device] = nil }
    public func accept(_ sample: Sample) -> Bool {
        if sample.cancelled || !(sample.x + sample.y).isFinite { presses[sample.device] = nil; return false }
        let previous = presses[sample.device]
        if let previous, previous.display != sample.display || sample.at < previous.last { presses[sample.device] = nil; return false }
        if sample.buttons == 0 {
            presses[sample.device] = nil
            return previous.map { $0.armed && !$0.rejected && sample.at - $0.started <= 5000 } ?? false
        }
        var press = previous ?? Press(sample)
        if sample.buttons & ~3 != 0 || sample.at - press.started > 5000 { press.rejected = true }
        if !press.armed && hypot(sample.x - press.x, sample.y - press.y) > 12 { press.rejected = true }
        if !press.armed && (press.buttons & sample.buttons) != press.buttons { press.rejected = true }
        if sample.buttons & 3 == 3 && !press.armed {
            if sample.at - press.started <= 150 && !press.rejected { press.armed = true }
            else { press.rejected = true }
        }
        press.buttons = sample.buttons; press.last = sample.at; presses[sample.device] = press
        return false
    }
}
