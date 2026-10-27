import Foundation
import EasySwitchCore

public struct SwitchSnapshot: Sendable {
    public var generation: UInt64
    public var online: Bool
    public var localLink: Bool?
    public var hostCount: Int
    public var currentHost: Int?
    public init(generation: UInt64, online: Bool, localLink: Bool?, hostCount: Int, currentHost: Int?) {
        self.generation = generation; self.online = online; self.localLink = localLink
        self.hostCount = hostCount; self.currentHost = currentHost
    }
}

public protocol DeviceSwitching: Sendable {
    func switchSnapshot(_ id: String) async -> SwitchSnapshot?
    func writeHost(_ id: String, target: Int) async throws
    func readHost(_ id: String) async throws -> HostInfo
}

public enum SwitchOutcome: String, Sendable {
    case alreadyCurrent, targetReported, localLinkClosed, unverified, stillOnSource, cancelled, failed

    var evidence: String {
        switch self {
        case .alreadyCurrent: "previous_local_channel_report"
        case .targetReported: "device_target_channel_report"
        case .localLinkClosed: "local_path_unavailable"
        case .stillOnSource: "device_source_channel_report"
        case .cancelled: "request_cancelled_or_generation_changed"
        case .unverified, .failed: "insufficient"
        }
    }
}

/// Latest destination wins; an in-flight OS write must finish before its successor starts.
public actor SwitchCoordinator {
    private struct Work { let token: UUID; let target: Int; let task: Task<SwitchOutcome, Never> }
    private let devices: any DeviceSwitching
    private let log: @Sendable (String) -> Void
    private let gate = AsyncKeyGate()
    private var work: [String: Work] = [:]
    private let verificationMS: Int
    private let retryMS: Int
    private let budgetMS: Int
    private let maxAttempts: Int
    private struct Metrics {
        let started = ContinuousClock.now
        var attempt = 0, writes = 0, sends = 0
        var queue: Int64 = 0, write: Int64 = 0, query: Int64 = 0
        var host: Int?
        var reason = "NA"
    }
    private var metrics: [UUID: Metrics] = [:]
    private var observers: [UUID: [@Sendable (SwitchProgress) -> Void]] = [:]
    private var latestProgress: [UUID: SwitchProgress] = [:]

    public init(devices: any DeviceSwitching, verificationMS: Int = 200, retryMS: Int = 100,
                budgetMS: Int = 9000, maxAttempts: Int = 6, log: @escaping @Sendable (String) -> Void) {
        self.devices = devices; self.verificationMS = verificationMS; self.retryMS = retryMS
        self.budgetMS = budgetMS; self.maxAttempts = max(1, maxAttempts); self.log = log
    }

    public func switchHost(_ id: String, target: Int, reason: String,
                           progress: (@Sendable (SwitchProgress) -> Void)? = nil) async -> SwitchOutcome {
        guard (0...2).contains(target) else { log("전환 생략: 목적지 범위 밖"); return .failed }
        if let previous = work[id], previous.target == target, !previous.task.isCancelled {
            if let progress {
                if let latest = latestProgress[previous.token], latest.outcome != nil { progress(latest) }
                else { observers[previous.token, default: []].append(progress); report(previous.token, stage: "JON") }
            }
            return await previous.task.value
        }
        let replaces = work[id] != nil
        work[id]?.task.cancel()
        let token = UUID()
        metrics[token] = Metrics()
        observers[token] = progress.map { [$0] } ?? []
        let task = Task { await self.execute(id, target: target, reason: reason, replaces: replaces, token: token) }
        work[id] = Work(token: token, target: target, task: task)
        let result = await task.value
        if work[id]?.token == token { work[id] = nil }
        observers[token] = nil; metrics[token] = nil; latestProgress[token] = nil
        return result
    }

    public func cancelAll(reason: String) {
        for (_, item) in work { item.task.cancel() }
        log("전환 작업 취소: \(reason)")
        // Keep running work registered until completion, including an in-flight write.
    }

    public func stop(reason: String) async {
        cancelAll(reason: reason)
        let running = work.values.map(\.task)
        for task in running { _ = await task.value }
    }

    private func execute(_ id: String, target: Int, reason: String, replaces: Bool, token: UUID) async -> SwitchOutcome {
        let clock = ContinuousClock()
        let start = clock.now
        await gate.acquire(id)
        metrics[token]?.queue = Self.ms(start.duration(to: .now))
        report(token, stage: "RUN")
        let outcome = await executeLocked(id, target: target, reason: reason, replaces: replaces, start: start, token: token)
        report(token, stage: "END", outcome: outcome)
        await gate.release(id)
        let elapsed = start.duration(to: clock.now)
        log("SWITCH_END | device=\(id) | target=\(target + 1) | result=\(outcome.rawValue) | evidence=\(outcome.evidence) | osWrites=\(metrics[token]?.writes ?? 0) | reason=\(metrics[token]?.reason ?? "NA") | remoteArrival=unobserved | total=\(elapsed)")
        return outcome
    }

    private func executeLocked(_ id: String, target: Int, reason: String, replaces: Bool,
                               start: ContinuousClock.Instant, token: UUID) async -> SwitchOutcome {
        guard !Task.isCancelled else { return .cancelled }
        guard let original = await devices.switchSnapshot(id), target < original.hostCount else {
            metrics[token]?.reason = "HOST"
            log("[\(id)] 전환 생략: 기기 또는 지원 채널 없음"); return .failed
        }
        guard original.online, original.localLink != false else {
            metrics[token]?.reason = "LINK"
            log("[\(id)] 전환 생략: 로컬 링크 없음"); return .unverified
        }
        if original.currentHost == target && !replaces {
            metrics[token]?.reason = "SAME"
            log("[\(id)] 전환 생략: 이미 채널 \(target + 1)"); return .alreadyCurrent
        }
        var writes = 0
        var sourceReported = false
        for attempt in 1...maxAttempts {
            metrics[token]?.attempt = attempt
            if Task.isCancelled { return .cancelled }
            if start.duration(to: .now) >= .milliseconds(budgetMS) {
                metrics[token]?.reason = "TIME"
                log("[\(id)] 전환 확인 예산 만료"); return .unverified
            }
            guard let before = await devices.switchSnapshot(id), before.generation == original.generation else { metrics[token]?.reason = "GEN"; return .cancelled }
            if !before.online || before.localLink == false { return writes > 0 ? .localLinkClosed : .unverified }
            do {
                let writeAt = ContinuousClock.now
                metrics[token]?.sends += 1
                report(token, stage: "SND")
                do { try await devices.writeHost(id, target: target) }
                catch { metrics[token]?.write += Self.ms(writeAt.duration(to: .now)); report(token, stage: "IOE"); throw error }
                let writeMS = Self.ms(writeAt.duration(to: .now))
                metrics[token]?.write += writeMS; metrics[token]?.writes += 1
                report(token, stage: "WRT", duration: writeMS)
                writes += 1
                log("SWITCH_WRITE | device=\(id) | target=\(target + 1) | attempt=\(attempt) | writes=\(writes) | evidence=os_write_completed | remoteArrival=unobserved | write=\(writeAt.duration(to: .now)) | reason=\(reason)")
                try Task.checkCancellation()
                // Observe departure during the delay instead of treating a write as completion.
                var waited = 0
                while waited < verificationMS {
                    try await Task.sleep(for: .milliseconds(min(100, verificationMS - waited)))
                    waited += min(100, verificationMS - waited)
                    if let state = await devices.switchSnapshot(id) {
                        if state.generation != original.generation { metrics[token]?.reason = "GEN"; return .cancelled }
                        if state.localLink == false { return .localLinkClosed }
                    } else { return .localLinkClosed }
                }
                report(token, stage: "CHK")
                let queryAt = ContinuousClock.now
                let host: HostInfo
                do { host = try await devices.readHost(id) }
                catch { metrics[token]?.query += Self.ms(queryAt.duration(to: .now)); report(token, stage: "NRP"); throw error }
                let queryMS = Self.ms(queryAt.duration(to: .now))
                metrics[token]?.query += queryMS; metrics[token]?.host = host.currentHost
                try Task.checkCancellation()
                guard let after = await devices.switchSnapshot(id), after.generation == original.generation else { metrics[token]?.reason = "GEN"; return .cancelled }
                if after.localLink == false { return .localLinkClosed }
                if host.currentHost == target { metrics[token]?.reason = "RPT"; report(token, stage: "TGT", duration: queryMS); return .targetReported }
                report(token, stage: "SRC", duration: queryMS)
                sourceReported = true
                log("[\(id)] 아직 채널 \(host.currentHost + 1) 보고; 시도 \(attempt)/\(maxAttempts)")
            } catch is CancellationError { return .cancelled }
            catch {
                sourceReported = false
                if let after = await devices.switchSnapshot(id) {
                    if after.generation != original.generation { metrics[token]?.reason = "GEN"; return .cancelled }
                    if after.localLink == false { return writes > 0 ? .localLinkClosed : .unverified }
                } else { return writes > 0 ? .localLinkClosed : .unverified }
                log("[\(id)] 전환 통신 오류 attempt=\(attempt): \(error)")
            }
            if attempt < maxAttempts {
                // Retry early, then leave longer gaps so a newly attached device can settle.
                let delay = min(1500, retryMS * (1 << min(attempt - 1, 4)))
                do { try await Task.sleep(for: .milliseconds(delay)) }
                catch { return .cancelled }
            }
        }
        return sourceReported ? .stillOnSource : .unverified
    }

    private func report(_ token: UUID, stage: String, duration: Int64 = 0, outcome: SwitchOutcome? = nil) {
        guard let m = metrics[token] else { return }
        let reason = m.reason != "NA" ? m.reason : outcome.map {
            switch $0 { case .localLinkClosed: "LEFT"; case .cancelled: "NEW"; case .stillOnSource, .unverified: "LIMIT"; case .failed: "ERR"; default: "NA" }
        } ?? "NA"
        let value = SwitchProgress(stage: stage, attempt: m.attempt, writes: m.writes, sends: m.sends,
                                   elapsedMS: Self.ms(m.started.duration(to: .now)), durationMS: duration,
                                   queueMS: m.queue, writeMS: m.write, queryMS: m.query, host: m.host, outcome: outcome, reason: reason)
        latestProgress[token] = value
        for observer in observers[token] ?? [] { observer(value) }
    }
    private static func ms(_ duration: Duration) -> Int64 {
        duration.components.seconds * 1000 + duration.components.attoseconds / 1_000_000_000_000_000
    }
}
