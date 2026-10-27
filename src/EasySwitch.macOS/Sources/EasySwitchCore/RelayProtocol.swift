import Foundation
import CryptoKit

public struct RelayMessage: Codable, Sendable, Equatable {
    public var version = 1
    public let id: UUID
    public let sender: UUID
    public let kind: String
    public let channel: Int
    public let timestamp: Int64
    public var acknowledgement: UUID?

    public init(sender: UUID, kind: String, channel: Int = 0, id: UUID = UUID(),
                timestamp: Int64 = Int64(Date().timeIntervalSince1970), acknowledgement: UUID? = nil) {
        self.id = id; self.sender = sender; self.kind = kind; self.channel = channel
        self.timestamp = timestamp; self.acknowledgement = acknowledgement
    }
}

/// AES-GCM combined format: a 12-byte nonce, ciphertext and 16-byte authentication tag.
/// Only authenticated, timely, bounded commands enter the replay cache.
public struct RelayProtocol: Sendable {
    public enum Failure: Error { case invalidKey, invalidMessage, expired, replay(RelayMessage), capacity }
    private let key: SymmetricKey
    private var seen: [UUID: Int64] = [:]
    public init(secret: Data) throws {
        guard secret.count == 32 else { throw Failure.invalidKey }
        key = SymmetricKey(data: secret)
    }
    public static func newSecret() -> Data { SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) } }
    public func seal(_ message: RelayMessage) throws -> Data {
        try Self.validate(message)
        return try AES.GCM.seal(JSONEncoder().encode(message), using: key,
                                authenticating: Data("EasySwitchRelay/1".utf8)).combined!
    }
    public mutating func open(_ packet: Data, now: Int64 = Int64(Date().timeIntervalSince1970)) throws -> RelayMessage {
        guard (29...4096).contains(packet.count) else { throw Failure.invalidMessage }
        let plaintext = try AES.GCM.open(AES.GCM.SealedBox(combined: packet), using: key,
                                         authenticating: Data("EasySwitchRelay/1".utf8))
        let message = try JSONDecoder().decode(RelayMessage.self, from: plaintext)
        try Self.validate(message)
        guard message.timestamp >= now - 15, message.timestamp <= now + 15 else { throw Failure.expired }
        seen = seen.filter { $0.value >= now - 30 }
        guard seen[message.id] == nil else { throw Failure.replay(message) }
        guard seen.count < 4096 else { throw Failure.capacity }
        seen[message.id] = now
        return message
    }
    private static func validate(_ message: RelayMessage) throws {
        guard message.version == 1,
              ["hello", "arrival", "ack"].contains(message.kind),
              message.kind == "arrival" ? (1...3).contains(message.channel) : message.channel == 0,
              message.kind != "ack" || message.acknowledgement != nil else { throw Failure.invalidMessage }
    }
}
