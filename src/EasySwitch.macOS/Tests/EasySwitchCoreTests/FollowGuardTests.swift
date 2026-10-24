import Testing
@testable import EasySwitchCore

@Suite("Preparation and connection generation parity")
struct FollowGuardTests {
    @Test func earlySignalWaitsForRoute() {
        var gate = EarlySignalGate<Int>()
        #expect(gate.isEmpty)
        let evicted = gate.hold(7, generation: 3, nowMilliseconds: 100)
        #expect(!evicted)
        let result = gate.open(currentGeneration: 3, nowMilliseconds: 110)
        #expect(result.frames == [7])
        #expect(result.discarded == 0)
        #expect(gate.isEmpty)
    }
    @Test func earlySignalNeverReplaysOlderConnection() {
        var gate = EarlySignalGate<Int>()
        gate.hold(1, generation: 1, nowMilliseconds: 100)
        gate.hold(2, generation: 2, nowMilliseconds: 101)
        let result = gate.open(currentGeneration: 2, nowMilliseconds: 102)
        #expect(result.frames == [2])
        #expect(result.discarded == 1)
    }
    @Test func earlySignalExpiresAtFiveSeconds() {
        var gate = EarlySignalGate<Int>()
        gate.hold(1, generation: 1, nowMilliseconds: 100)
        gate.hold(2, generation: 1, nowMilliseconds: 101)
        let result = gate.open(currentGeneration: 1, nowMilliseconds: 5_100)
        #expect(result.frames == [2])
        #expect(result.discarded == 1)
    }
    @Test func earlySignalDropsTimeRegression() {
        var gate = EarlySignalGate<Int>()
        gate.hold(1, generation: 1, nowMilliseconds: 100)
        let result = gate.open(currentGeneration: 1, nowMilliseconds: 99)
        #expect(result.discarded == 1)
    }
    @Test func capacityEvictsOldestWithoutReordering() {
        var gate = EarlySignalGate<Int>(capacity: 2)
        let first = gate.hold(1, generation: 1, nowMilliseconds: 0)
        let second = gate.hold(2, generation: 1, nowMilliseconds: 1)
        let third = gate.hold(3, generation: 1, nowMilliseconds: 2)
        #expect(!first)
        #expect(!second)
        #expect(third)
        #expect(gate.count == 2)
        let result = gate.open(currentGeneration: 1, nowMilliseconds: 3)
        #expect(result.frames == [2, 3])
    }
    @Test func onlyActiveManualEchoIsSuppressed() {
        var guardModel = ManualSwitchGuard()
        guardModel.begin(generation: 10, target: 2)
        #expect(guardModel.matches(generation: 10, target: 2))
        #expect(!guardModel.matches(generation: 10, target: 1))
        #expect(!guardModel.matches(generation: 11, target: 2))
        guardModel.complete(generation: 9)
        #expect(guardModel.matches(generation: 10, target: 2))
        guardModel.complete(generation: 10)
        #expect(!guardModel.matches(generation: 10, target: 2))
    }
    @Test func olderCompletionDoesNotClearNewRequest() {
        var guardModel = ManualSwitchGuard()
        guardModel.begin(generation: 10, target: 0)
        guardModel.begin(generation: 11, target: 1)
        guardModel.complete(generation: 10)
        #expect(guardModel.matches(generation: 11, target: 1))
    }
    @Test func preparationWaitsTenSecondsInclusively() {
        #expect(!FollowPreparation.expired(started: 50, now: 10_050))
        #expect(FollowPreparation.expired(started: 50, now: 10_051))
    }
    @Test func preparationExcludesNeverLocallyObservedMouse() {
        #expect(FollowPreparation.readiness(exists: true, transport: true, localLink: false, online: false,
                                           hosts: 3, observedLocally: false) == .exclude)
    }
    @Test func preparationWaitsForReturningKnownMouse() {
        #expect(FollowPreparation.readiness(exists: true, transport: true, localLink: false, online: false,
                                           hosts: 3, observedLocally: true) == .wait)
    }
    @Test func preparationRequiresUsableRouteAndHosts() {
        #expect(FollowPreparation.readiness(exists: true, transport: true, localLink: true, online: true,
                                           hosts: 3, observedLocally: true) == .ready)
        #expect(FollowPreparation.readiness(exists: true, transport: true, localLink: true, online: true,
                                           hosts: 0, observedLocally: true) == .wait)
        #expect(FollowPreparation.readiness(exists: true, transport: false, localLink: true, online: true,
                                           hosts: 3, observedLocally: true) == .wait)
    }
}
