import Foundation
import Testing
import EasySwitchCore
@testable import EasySwitchEngine

private actor SwitchingDevice: DeviceSwitching {
    enum Behavior: Sendable { case source, target, targetAfterFourthWrite, noReply, sourceThenNoReply, leaveLocal, newGeneration }
    private var state: SwitchSnapshot?
    private let behavior: Behavior
    private var blockFirst: Bool
    private var blocked: [CheckedContinuation<Void, Never>] = []
    private var targets: [Int] = []
    private var reads = 0
    private var active = 0
    private var maximumActive = 0

    init(behavior: Behavior = .target, current: Int? = 0, hosts: Int = 3,
         online: Bool = true, localLink: Bool? = true, blockFirst: Bool = false) {
        self.behavior = behavior
        self.blockFirst = blockFirst
        state = SwitchSnapshot(generation: 1, online: online, localLink: localLink, hostCount: hosts, currentHost: current)
    }
    func switchSnapshot(_ id: String) async -> SwitchSnapshot? { state }
    func writeHost(_ id: String, target: Int) async throws {
        active += 1
        maximumActive = max(maximumActive, active)
        targets.append(target)
        if blockFirst {
            blockFirst = false
            await withCheckedContinuation { blocked.append($0) }
        }
        active -= 1
        switch behavior {
        case .leaveLocal: state?.localLink = false; state?.online = false
        case .newGeneration: state?.generation += 1
        case .target: state?.currentHost = target
        case .targetAfterFourthWrite: if targets.count >= 4 { state?.currentHost = target }
        case .source, .noReply, .sourceThenNoReply: break
        }
    }
    func readHost(_ id: String) async throws -> HostInfo {
        reads += 1
        if behavior == .noReply || (behavior == .sourceThenNoReply && reads > 1) { throw EngineError.timeout }
        guard let state else { throw EngineError.disconnected }
        return HostInfo(hostCount: state.hostCount, currentHost: state.currentHost ?? 0)
    }
    func releaseWrite() {
        let continuations = blocked
        blocked = []
        for continuation in continuations { continuation.resume() }
    }
    var writeTargets: [Int] { targets }
    var readCount: Int { reads }
    var maxConcurrentWrites: Int { maximumActive }
}

private func coordinator(_ devices: SwitchingDevice, budgetMS: Int = 500) -> SwitchCoordinator {
    SwitchCoordinator(devices: devices, verificationMS: 1, retryMS: 1, budgetMS: budgetMS, maxAttempts: 3, log: { _ in })
}

private func switchEventually(_ condition: @escaping @Sendable () async -> Bool) async -> Bool {
    for _ in 0..<500 {
        if await condition() { return true }
        try? await Task.sleep(for: .milliseconds(1))
    }
    return false
}

@Suite("Switch verification and latest destination")
struct SwitchCoordinatorTests {
    @Test func currentHostIsSkippedWithoutWriting() async {
        let devices = SwitchingDevice(current: 1)
        let result = await coordinator(devices).switchHost("mouse", target: 1, reason: "test")
        #expect(result == .alreadyCurrent)
        #expect(await devices.writeTargets.isEmpty)
    }
    @Test func unsupportedChannelDoesNotWrite() async {
        let devices = SwitchingDevice(hosts: 1)
        let result = await coordinator(devices).switchHost("mouse", target: 2, reason: "test")
        #expect(result == .failed)
        #expect(await devices.writeTargets.isEmpty)
    }
    @Test func offlineDeviceIsUnverifiedWithoutWriting() async {
        let devices = SwitchingDevice(online: false, localLink: false)
        let result = await coordinator(devices).switchHost("mouse", target: 1, reason: "test")
        #expect(result == .unverified)
        #expect(await devices.writeTargets.isEmpty)
    }
    @Test func localDepartureIsEvidenceDistinctFromTargetArrival() async {
        let devices = SwitchingDevice(behavior: .leaveLocal)
        let result = await coordinator(devices).switchHost("mouse", target: 1, reason: "test")
        #expect(result == .localLinkClosed)
        #expect(await devices.writeTargets == [1])
        #expect(await devices.readCount == 0)
    }
    @Test func writeSuccessAloneRemainsUnverifiedAfterThreeAttempts() async {
        let devices = SwitchingDevice(behavior: .noReply)
        let result = await coordinator(devices).switchHost("mouse", target: 1, reason: "test")
        #expect(result == .unverified)
        #expect(await devices.writeTargets == [1, 1, 1])
        #expect(await devices.readCount == 3)
    }
    @Test func sourceStillReportedIsDistinctFromMissingReply() async {
        let devices = SwitchingDevice(behavior: .source)
        let result = await coordinator(devices).switchHost("mouse", target: 2, reason: "test")
        #expect(result == .stillOnSource)
        #expect(await devices.writeTargets == [2, 2, 2])
    }
    @Test func earlierSourceReportCannotTurnLaterMissingRepliesIntoConfirmedFailure() async {
        let devices = SwitchingDevice(behavior: .sourceThenNoReply)
        let result = await coordinator(devices).switchHost("mouse", target: 2, reason: "test")
        #expect(result == .unverified)
        #expect(await devices.writeTargets == [2, 2, 2])
        #expect(await devices.readCount == 3)
    }
    @Test func targetHostReportCompletesAfterOneWrite() async {
        let devices = SwitchingDevice(behavior: .target)
        let result = await coordinator(devices).switchHost("mouse", target: 1, reason: "test")
        #expect(result == .targetReported)
        #expect(await devices.writeTargets == [1])
    }
    @Test func earlyRetriesStillAllowADeviceToSettleBeyondThreeWrites() async {
        let devices = SwitchingDevice(behavior: .targetAfterFourthWrite)
        let engine = SwitchCoordinator(devices: devices, verificationMS: 1, retryMS: 1, budgetMS: 500, log: { _ in })
        #expect(await engine.switchHost("keyboard", target: 1, reason: "settling device") == .targetReported)
        #expect(await devices.writeTargets == [1, 1, 1, 1])
    }
    @Test func changedConnectionGenerationCancelsVerification() async {
        let devices = SwitchingDevice(behavior: .newGeneration)
        let result = await coordinator(devices).switchHost("mouse", target: 1, reason: "test")
        #expect(result == .cancelled)
        #expect(await devices.writeTargets == [1])
        #expect(await devices.readCount == 0)
    }
    @Test func zeroBudgetCannotSendAnUnboundedWrite() async {
        let devices = SwitchingDevice()
        let result = await coordinator(devices, budgetMS: 0).switchHost("mouse", target: 1, reason: "test")
        #expect(result == .unverified)
        #expect(await devices.writeTargets.isEmpty)
    }
    @Test func laterDestinationCancelsEarlierAndWaitsForItsWrite() async throws {
        let devices = SwitchingDevice(blockFirst: true)
        let engine = coordinator(devices)
        let first = Task { await engine.switchHost("mouse", target: 1, reason: "first") }
        #expect(await switchEventually { await devices.writeTargets == [1] })
        let second = Task { await engine.switchHost("mouse", target: 2, reason: "newer") }
        try await Task.sleep(for: .milliseconds(10))
        #expect(await devices.writeTargets == [1])
        await devices.releaseWrite()
        #expect(await first.value == .cancelled)
        #expect(await second.value == .targetReported)
        #expect(await devices.writeTargets == [1, 2])
        #expect(await devices.maxConcurrentWrites == 1)
    }
    @Test func identicalDestinationJoinsActiveRequest() async throws {
        let devices = SwitchingDevice(blockFirst: true)
        let engine = coordinator(devices)
        let first = Task { await engine.switchHost("mouse", target: 2, reason: "first") }
        #expect(await switchEventually { await devices.writeTargets == [2] })
        let second = Task { await engine.switchHost("mouse", target: 2, reason: "same") }
        try await Task.sleep(for: .milliseconds(10))
        await devices.releaseWrite()
        #expect(await first.value == .targetReported)
        #expect(await second.value == .targetReported)
        #expect(await devices.writeTargets == [2])
    }
    @Test func cancelAllDuringWritePreventsFurtherAttempts() async {
        let devices = SwitchingDevice(behavior: .source, blockFirst: true)
        let engine = coordinator(devices)
        let first = Task { await engine.switchHost("mouse", target: 1, reason: "first") }
        #expect(await switchEventually { await devices.writeTargets == [1] })
        await engine.cancelAll(reason: "settings changed")
        await devices.releaseWrite()
        #expect(await first.value == .cancelled)
        #expect(await devices.writeTargets == [1])
        #expect(await devices.readCount == 0)
    }

    @Test func sameDestinationAfterCancellationStartsFreshWork() async throws {
        let devices = SwitchingDevice(blockFirst: true)
        let engine = coordinator(devices)
        let first = Task { await engine.switchHost("mouse", target: 1, reason: "old") }
        #expect(await switchEventually { await devices.writeTargets == [1] })
        await engine.cancelAll(reason: "selection changed")
        let second = Task { await engine.switchHost("mouse", target: 1, reason: "new") }
        try await Task.sleep(for: .milliseconds(10))
        #expect(await devices.writeTargets == [1])
        await devices.releaseWrite()
        #expect(await first.value == .cancelled)
        #expect(await second.value == .targetReported)
        #expect(await devices.writeTargets == [1, 1])
        #expect(await devices.maxConcurrentWrites == 1)
    }
}
