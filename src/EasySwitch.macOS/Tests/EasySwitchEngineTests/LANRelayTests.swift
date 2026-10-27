import Foundation
import Network
import Testing
import EasySwitchCore
@testable import EasySwitchEngine

private actor RelayObservations {
    var port: UInt16?
    var arrivals: [Int] = []
    var received: [Int] = []
    var acknowledgements = 0
    func listening(_ value: UInt16) { port = value }
    func arrived(_ value: Int) { arrivals.append(value) }
    func packet(_ message: RelayMessage) {
        if message.kind == "ack" { acknowledgements += 1 }
        if message.kind == "arrival" { received.append(message.channel) }
    }
}

private final class RelayPeer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "EasySwitch.TestRelay")
    private let connection: NWConnection
    private var codec: RelayProtocol
    private let observations: RelayObservations
    let id = UUID()
    init(port: UInt16, secret: Data, observations: RelayObservations) throws {
        connection = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .udp)
        codec = try RelayProtocol(secret: secret); self.observations = observations
        connection.stateUpdateHandler = { [weak self] state in if case .ready = state { self?.receive() } }
        connection.start(queue: queue)
    }
    func stop() { connection.cancel() }
    func send(_ message: RelayMessage) {
        queue.async { [self] in
            guard let data = try? codec.seal(message) else { return }
            connection.send(content: data, completion: .contentProcessed { _ in })
        }
    }
    private func receive() {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data, let message = try? self.codec.open(data) {
                Task { await self.observations.packet(message) }
                if message.kind == "arrival" { self.send(.init(sender: self.id, kind: "ack", acknowledgement: message.id)) }
            }
            if error == nil { self.receive() }
        }
    }
}

private func relayEventually(_ condition: () async -> Bool) async -> Bool {
    for _ in 0..<1000 {
        if await condition() { return true }
        try? await Task.sleep(for: .milliseconds(5))
    }
    return false
}

@Suite("Authenticated UDP relay on real loopback sockets")
struct LANRelayTests {
    @Test func peerHandshakeBidirectionalArrivalAndReplaySuppression() async throws {
        let state = RelayObservations(), secret = Data(0..<32)
        let relay = try LANRelay(id: UUID(), secret: secret, log: { _ in },
            onArrival: { await state.arrived($0) }, onListening: { port in Task { await state.listening(port) } })
        relay.start(port: 0)
        #expect(await relayEventually { await state.port != nil })
        let port = try #require(await state.port)
        let peer = try RelayPeer(port: port, secret: secret, observations: state)
        defer { peer.stop(); relay.stop() }
        peer.send(.init(sender: peer.id, kind: "hello"))
        #expect(await relayEventually { await state.acknowledgements > 0 })
        relay.keyboardArrived(channel: 2)
        #expect(await relayEventually { await state.received == [2] })
        let arrival = RelayMessage(sender: peer.id, kind: "arrival", channel: 3)
        peer.send(arrival)
        #expect(await relayEventually { await state.arrivals == [3] })
        let before = await state.acknowledgements
        peer.send(arrival)
        #expect(await relayEventually { await state.acknowledgements > before })
        #expect(await state.arrivals == [3])
    }
}
