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
