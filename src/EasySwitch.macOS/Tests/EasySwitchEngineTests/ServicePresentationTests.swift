import Foundation
import Testing
@testable import EasySwitchEngine

@Suite("System engine presentation acknowledgement")
struct ServicePresentationTests {
    @Test func callerMustAcknowledgeTheExactPreparedNotice() async throws {
        let gate = ServicePresentationGate()
        let event = EngineEvent.notice(run: 42, target: 1, rows: [.init(id: "mouse", name: "Mouse", kind: "마우스", state: .switching)], connection: false)
        let delivery = Task { await gate.present(event) }
        let prepared = await gate.prepared()
        #expect(prepared.token != nil)
        let decoded = try JSONDecoder().decode(ServicePreparedSwitch.self, from: JSONEncoder().encode(prepared))
        if case .notice(let run, let target, let rows, _) = decoded.notice {
            #expect(run == 42 && target == 1 && rows.first?.id == "mouse")
        } else { Issue.record("Missing prepared notice") }
        await gate.acknowledge()
        #expect(await delivery.value)
    }
    @Test func pollSkipsEventsFromAnOlderHelperAndKeepsTheRest() throws {
        let json = #"{"sequence":7,"logs":["line"],"events":[{"receivers":{"_0":[]}},{"status":{"_0":"ok"}},{"pairing":{"_0":{}}}]}"#
        let poll = try JSONDecoder().decode(ServicePoll.self, from: Data(json.utf8))
        #expect(poll.sequence == 7 && poll.logs == ["line"] && poll.events.count == 1)
        if case .status(let value) = poll.events.first { #expect(value == "ok") } else { Issue.record("Lost the known event") }
    }
    @Test func pollRoundTripsThroughTheHelperEncoding() throws {
        let sent = ServicePoll(sequence: 3, events: [.status("ok"), .keyboardDeparture(target: 1)], logs: [])
        let poll = try JSONDecoder().decode(ServicePoll.self, from: JSONEncoder().encode(sent))
        #expect(poll.sequence == 3 && poll.events.count == 2)
    }
    @Test func expiryRejectsBothWaitingAndFuturePresentationInsteadOfAllowingWrites() async {
        let gate = ServicePresentationGate()
        let delivery = Task { await gate.present(.keyboardDeparture(target: 1)) }
        _ = await gate.prepared()
        await gate.expire()
        #expect(await delivery.value == false)
        #expect(await gate.present(.keyboardDeparture(target: 2)) == false)
        #expect(await gate.prepared().token == nil)
    }
    @Test func requestWithoutEligibleDevicesCompletesWithoutAPresentationWait() async {
        let gate = ServicePresentationGate()
        await gate.finish()
        #expect(await gate.prepared().token == nil)
    }
}
