import Foundation

public enum NoticeState: String, Codable, Sendable { case switching, complete, failed, unconfirmed, connected }
public struct NoticeRow: Codable, Sendable, Identifiable {
    public let id: String
    public let name: String
    public let kind: String
    public let state: NoticeState
    public init(id: String, name: String, kind: String, state: NoticeState) {
        self.id = id; self.name = name; self.kind = kind; self.state = state
    }
}
public struct SwitchProgress: Sendable {
    public let stage: String
    public let attempt: Int
    public let writes: Int
    public let sends: Int
    public let elapsedMS: Int64
    public let durationMS: Int64
    public let queueMS: Int64
    public let writeMS: Int64
    public let queryMS: Int64
    public let host: Int?
    public let outcome: SwitchOutcome?
    public let reason: String
}

public enum SwitchCode {
    public static func format(request: Int, source: String, target: Int, role: String, progress p: SwitchProgress,
                              requestElapsedMS: Int64? = nil) -> String {
        let outcome: String
        let verdict: String
        switch p.outcome {
        case .alreadyCurrent: outcome = "AC"; verdict = "S"
        case .targetReported: outcome = "TR"; verdict = "S"
        case .localLinkClosed: outcome = "LC"; verdict = "U"
        case .unverified: outcome = "UV"; verdict = "U"
        case .stillOnSource: outcome = "SS"; verdict = "F"
        case .cancelled: outcome = "CA"; verdict = "C"
        case .failed: outcome = "ER"; verdict = "F"
        case nil: outcome = "NA"; verdict = "P"
        }
        return "ES1|V0213|R\(String(format: "%04d", request))|\(source)\(target + 1)|\(role)|\(p.stage)|\(verdict)|\(outcome)|\(p.reason)|E\(requestElapsedMS ?? p.elapsedMS)|D\(p.elapsedMS)|I\(p.durationMS)|Q\(p.queueMS)|WT\(p.writeMS)|QT\(p.queryMS)|A\(p.attempt)|N\(p.sends)|RT\(max(0, p.sends - 1))|W\(p.writes)|H\(p.host.map { $0 + 1 } ?? 0)"
    }
}
