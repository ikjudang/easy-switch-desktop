import Foundation
import Network
import EasySwitchCore

/// All network state is confined to queue. Each retry has the same request ID but a new GCM nonce.
public final class LANRelay: @unchecked Sendable {
    private let queue = DispatchQueue(label: "EasySwitch.LAN")
    private let localID: UUID
    private let secret: Data
    private var codec: RelayProtocol
    private var listener: NWListener?
    private var peer: NWConnection?
    private var peerID: UUID?
    private var connections: [UUID: NWConnection] = [:]
    private var outstanding: [UUID: Int] = [:]
    private var generation = 0
    private let log: @Sendable (String) -> Void
    private let onListening: (@Sendable (UInt16) -> Void)?
    private let arrival: @Sendable (Int) async -> Void

    public init(id: UUID, secret: Data, log: @escaping @Sendable (String) -> Void,
                onArrival: @escaping @Sendable (Int) async -> Void, onListening: (@Sendable (UInt16) -> Void)? = nil) throws {
        localID = id; self.secret = secret; codec = try RelayProtocol(secret: secret)
        self.log = log; arrival = onArrival; self.onListening = onListening
    }

    public func start(port: UInt16 = 39821) {
        queue.async { [self] in
            guard listener == nil else { return }
            do {
                let parameters = NWParameters.udp
                let listener = try NWListener(using: parameters, on: NWEndpoint.Port(rawValue: port)!)
                self.listener = listener
                listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
                let token = generation
                listener.stateUpdateHandler = { [weak self] state in
                    if case .failed(let error) = state, let self, self.generation == token {
                        self.log("LAN 수신 실패: \(error); 재시도 대기")
                        self.listener?.cancel(); self.listener = nil
                        self.queue.asyncAfter(deadline: .now() + 1) { [weak self] in
                            guard let self, self.generation == token else { return }; self.start(port: port)
                        }
                    }
                    if case .ready = state, let self {
                        self.log("LAN 인증 수신 시작: port=\(self.listener?.port?.rawValue ?? port)")
                        if let actual = self.listener?.port?.rawValue { self.onListening?(actual) }
                    }
                }
                listener.start(queue: queue)
            } catch { log("LAN 시작 실패: \(error)") }
        }
    }
    public func stop() {
        queue.async { [self] in
            generation += 1; listener?.cancel(); listener = nil
            connections.values.forEach { $0.cancel() }; connections.removeAll()
            peer = nil; peerID = nil; outstanding.removeAll()
        }
    }
    public func keyboardArrived(channel: Int) {
        queue.async { [self] in
            guard (1...3).contains(channel), let peer else { log("LAN 도착 알림 생략: 인증된 태블릿 연결 없음"); return }
            let message = RelayMessage(sender: localID, kind: "arrival", channel: channel)
            outstanding[message.id] = 0
            sendWithRetry(message, connection: peer, generation: generation)
        }
    }
    private func accept(_ connection: NWConnection) {
        guard connections.count < 32 else { connection.cancel(); return }
        let token = UUID()
        connections[token] = connection
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection else { return }
            if case .ready = state { self.receive(connection) }
            if case .failed = state { self.remove(token, connection) }
            if case .cancelled = state { self.remove(token, connection) }
        }
        connection.start(queue: queue)
        // Unauthenticated UDP endpoints cannot occupy listener capacity indefinitely.
        queue.asyncAfter(deadline: .now() + 20) { [weak self, weak connection] in
            guard let self, let connection, self.peer !== connection else { return }
            self.remove(token, connection); connection.cancel()
        }
    }
    private func remove(_ token: UUID, _ connection: NWConnection) {
        connections[token] = nil
        if peer === connection { peer = nil; peerID = nil }
    }
    private func receive(_ connection: NWConnection) {
        connection.receiveMessage { [weak self, weak connection] data, _, _, error in
            guard let self, let connection else { return }
            if let data {
                do {
                    let message = try self.codec.open(data)
                    guard message.sender != self.localID else { throw RelayProtocol.Failure.invalidMessage }
                    if message.kind == "hello" {
                        // Possession of the screen-transferred 256-bit key authenticates this peer.
                        if let peerID = self.peerID, peerID != message.sender { throw RelayProtocol.Failure.invalidMessage }
                        let previous = self.peer
                        self.peer = connection; self.peerID = message.sender
                        if previous !== connection { previous?.cancel() }
                        self.log("LAN 태블릿 인증 완료")
                        self.acknowledge(message, connection)
                    } else if message.sender == self.peerID {
                        if message.kind == "ack", let id = message.acknowledgement {
                            self.outstanding[id] = nil; self.log("LAN 도착 알림 접수 확인")
                        } else if message.kind == "arrival" {
                            self.acknowledge(message, connection)
                            Task { await self.arrival(message.channel) }
                        }
                    }
                } catch RelayProtocol.Failure.replay(let message) {
                    if message.sender == self.peerID, message.kind != "ack" { self.acknowledge(message, connection) }
                } catch { self.log("LAN 패킷 거부: 인증·시간·형식 검사 실패") }
            }
            if error == nil { self.receive(connection) }
        }
    }
    private func acknowledge(_ request: RelayMessage, _ connection: NWConnection) {
        guard let packet = try? codec.seal(RelayMessage(sender: localID, kind: "ack", acknowledgement: request.id)) else { return }
        connection.send(content: packet, completion: .contentProcessed { _ in })
    }
    private func sendWithRetry(_ message: RelayMessage, connection: NWConnection, generation token: Int) {
        guard token == generation, let attempt = outstanding[message.id] else { return }
        guard attempt < 3 else { outstanding[message.id] = nil; log("LAN 도착 알림 확인 만료"); return }
        guard let packet = try? codec.seal(message) else { log("LAN 도착 알림 생성 실패"); return }
        outstanding[message.id] = attempt + 1
        connection.send(content: packet, completion: .contentProcessed { [log] error in
            if let error { log("LAN 전송 실패: \(error)") }
        })
        queue.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.sendWithRetry(message, connection: connection, generation: token) }
    }
}
