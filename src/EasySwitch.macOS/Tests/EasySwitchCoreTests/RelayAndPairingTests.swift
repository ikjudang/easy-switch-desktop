import Foundation
import Testing
@testable import EasySwitchCore

@Suite("Authenticated LAN and receiver registration")
struct RelayAndPairingTests {
    @Test func authenticatedArrivalRejectsTamperingWrongKeysExpiryAndReplay() throws {
        let secret = Data(0..<32)
        let sender = UUID()
        let message = RelayMessage(sender: sender, kind: "arrival", channel: 2, timestamp: 1000)
        let codec = try RelayProtocol(secret: secret)
        let packet = try codec.seal(message)
        var receiver = try RelayProtocol(secret: secret)
        #expect(try receiver.open(packet, now: 1000) == message)
        #expect(throws: (any Error).self) { try receiver.open(packet, now: 1000) }
        var wrongKey = try RelayProtocol(secret: Data(repeating: 0, count: 32))
        #expect(throws: (any Error).self) { try wrongKey.open(packet, now: 1000) }
        var expired = try RelayProtocol(secret: secret)
        #expect(throws: (any Error).self) { try expired.open(packet, now: 1016) }
        var damaged = packet; damaged[15] ^= 1
        #expect(throws: (any Error).self) { try expired.open(damaged, now: 1000) }
        // A failed authentication cannot consume a valid command ID.
        #expect(try expired.open(packet, now: 1000) == message)
    }
    @Test func invalidArrivalAndKeysCannotProducePackets() throws {
        #expect(throws: (any Error).self) { try RelayProtocol(secret: Data(repeating: 1, count: 16)) }
        let codec = try RelayProtocol(secret: Data(repeating: 0, count: 32))
        #expect(throws: (any Error).self) { try codec.seal(.init(sender: UUID(), kind: "arrival", channel: 4)) }
        #expect(throws: (any Error).self) { try codec.seal(.init(sender: UUID(), kind: "switch", channel: 1)) }
        #expect(throws: (any Error).self) { try codec.seal(.init(sender: UUID(), kind: "ack")) }
    }
    @Test func unifyingSearchNeverRemovesSlotsAndExistingConnectionsDoNotCompletePairing() throws {
        var pairing = ReceiverPairing(receiverID: "receiver", bolt: false, knownSlots: [1])
        #expect(try pairing.search().bytes == [0x10, 0xFF, 0x80, 0xB2, 1, 0, 30])
        #expect(try pairing.cancel().bytes == [0x10, 0xFF, 0x80, 0xB2, 2, 0, 0])
        let existing = try HidppFrame(reportType: .short, deviceIndex: 1, featureIndex: 0x41, funcSwByte: 1, parameters: [0, 1, 2])
        pairing.accept(existing); #expect(pairing.progress.stage == "searching")
        let added = try HidppFrame(reportType: .short, deviceIndex: 2, featureIndex: 0x41, funcSwByte: 1, parameters: [0, 1, 2])
        pairing.accept(added); #expect(pairing.progress.stage == "complete" && pairing.progress.slot == 2)
    }
    @Test func boltRequiresDiscoveryAndExplicitConfirmationBeforeAuthentication() throws {
        var pairing = ReceiverPairing(receiverID: "bolt", bolt: true)
        #expect(try pairing.search().bytes == [0x10, 0xFF, 0x80, 0xC0, 30, 1, 0])
        #expect(throws: (any Error).self) { try pairing.confirm() }
        var details = [UInt8](repeating: 0, count: 16)
        details[3] = 1; details[6...11] = [1, 2, 3, 4, 5, 6]; details[14] = 1
        pairing.accept(try .init(reportType: .long, deviceIndex: 0xFF, featureIndex: 0x4F, funcSwByte: 7, parameters: details))
        var name = [UInt8](repeating: 0, count: 16); name[1] = 1; name[2] = 3; name[3...5] = Array("MXK".utf8)[...]
        pairing.accept(try .init(reportType: .long, deviceIndex: 0xFF, featureIndex: 0x4F, funcSwByte: 8, parameters: name))
        #expect(pairing.progress.stage == "searching")
        pairing.accept(try .init(reportType: .long, deviceIndex: 0xFF, featureIndex: 0x4F, funcSwByte: 7, parameters: name))
        #expect(pairing.progress.stage == "candidate" && pairing.progress.deviceName == "MXK")
        let command = try pairing.confirm()
        #expect(command.featureIndex == 0x82 && command.funcSwByte == 0xC1)
        #expect(Array(command.parameters.prefix(10)) == [1, 0, 1, 2, 3, 4, 5, 6, 1, 20])
        #expect(try pairing.cancel().featureIndex == 0x82)
        var passkey = [UInt8](repeating: 0, count: 16); passkey[0...5] = Array("123456".utf8)[...]
        pairing.accept(try .init(reportType: .long, deviceIndex: 0xFF, featureIndex: 0x4D, funcSwByte: 0, parameters: passkey))
        #expect(pairing.progress.instruction.contains("123456"))
        var completed = [UInt8](repeating: 0, count: 16); completed[7] = 4
        pairing.accept(try .init(reportType: .long, deviceIndex: 0xFF, featureIndex: 0x54, funcSwByte: 2, parameters: completed))
        #expect(pairing.progress.stage == "complete" && pairing.progress.slot == 4)
    }
}
