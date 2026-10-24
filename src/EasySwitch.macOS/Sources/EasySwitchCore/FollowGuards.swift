import Foundation

/// A value-type queue intended to be owned by the connection actor; no UI work or wall clock is involved.
public struct EarlySignalGate<Element: Sendable>: Sendable {
    private struct Entry: Sendable {
        let frame: Element
        let generation: UInt64
        let at: Int64
    }
    private var held: [Entry] = []
    private let capacity: Int
    private let lifetimeMilliseconds: Int64

    public init(capacity: Int = 16, lifetimeMilliseconds: Int64 = 5_000) {
        self.capacity = max(1, capacity)
        self.lifetimeMilliseconds = lifetimeMilliseconds
    }
    public var isEmpty: Bool { held.isEmpty }
    public var count: Int { held.count }

    /// Returns true if the oldest report was evicted. The caller should log this loss.
    @discardableResult public mutating func hold(_ frame: Element, generation: UInt64, nowMilliseconds: Int64) -> Bool {
        let evicted = held.count >= capacity
        if evicted { held.removeFirst() }
        held.append(Entry(frame: frame, generation: generation, at: nowMilliseconds))
        return evicted
    }

    public mutating func open(currentGeneration: UInt64, nowMilliseconds: Int64) -> (frames: [Element], discarded: Int) {
        var frames: [Element] = []
        var discarded = 0
        for entry in held {
            let age = nowMilliseconds - entry.at
            if entry.generation != currentGeneration || age < 0 || age >= lifetimeMilliseconds { discarded += 1 }
            else { frames.append(entry.frame) }
        }
        held.removeAll(keepingCapacity: true)
        return (frames, discarded)
    }
}

/// Suppress only an active request's echo, so a real new departure after reconnection remains visible.
public struct ManualSwitchGuard: Sendable {
    private var active: (generation: UInt64, target: Int)?
    public init() {}
    public mutating func begin(generation: UInt64, target: Int) { active = (generation, target) }
    public func matches(generation: UInt64, target: Int) -> Bool {
        active?.generation == generation && active?.target == target
    }
    public mutating func complete(generation: UInt64) {
        if active?.generation == generation { active = nil }
    }
}

public enum FollowReadiness: Sendable { case wait, ready, exclude }
public enum FollowPreparation {
    public static let waitMilliseconds: Int64 = 10_000
    public static func expired(started: Int64, now: Int64) -> Bool { now - started > waitMilliseconds }
    public static func readiness(exists: Bool, transport: Bool, localLink: Bool?, online: Bool,
                                 hosts: Int, observedLocally: Bool) -> FollowReadiness {
        if localLink == false && !observedLocally { return .exclude }
        return exists && transport && localLink != false && online && hosts > 0 ? .ready : .wait
    }
}
