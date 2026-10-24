import Foundation
import Testing
@testable import EasySwitchCore

private func bytes(_ hex: String) -> [UInt8] {
    guard let value = HidppHex.parse(hex) else {
        Issue.record("Invalid literal hex fixture: \(hex)")
        return []
    }
    return value
}
private func frame(_ hex: String) throws -> HidppFrame { try #require(HidppParser.parse(bytes(hex))) }

@Suite("HID++ frame, features and receiver parity")
struct HidppTests {
    @Test func shortLayoutAndNibbles() throws {
        let value = try HidppFrame.build(featureIndex: 5, functionID: 3, softwareID: 10, parameters: [2])
        #expect(value.bytes == bytes("10 FF 05 3A 02 00 00"))
        #expect(value.functionID == 3)
        #expect(value.softwareID == 10)
        #expect(value.param(-1) == 0)
        #expect(value.param(99) == 0)
    }
    @Test(arguments: HidppReportType.allCases) func roundTrip(_ type: HidppReportType) throws {
        let original = try HidppFrame.build(featureIndex: 7, functionID: 1, reportType: type, softwareID: 9, parameters: [1])
        #expect(HidppParser.parse(original.bytes) == original)
    }
    @Test func toLongPreservesHeaderAndPadding() throws {
        let short = try RootFeature.getFeature(0x1814, softwareID: 9)
        let long = short.toLong()
        #expect(long.bytes == bytes("11 FF 00 09 18 14 00 00 00 00 00 00 00 00 00 00 00 00 00 00"))
        #expect(long.toLong() == long)
    }
    @Test func echoesAllThreeFields() throws {
        let response = try HidppFrame.build(featureIndex: 0, functionID: 0, softwareID: 9)
        #expect(response.echoes(deviceIndex: 255, featureIndex: 0, funcSwByte: 9))
        #expect(!response.echoes(deviceIndex: 255, featureIndex: 0, funcSwByte: 10))
        #expect(!response.echoes(deviceIndex: 1, featureIndex: 0, funcSwByte: 9))
        #expect(!response.echoes(deviceIndex: 255, featureIndex: 1, funcSwByte: 9))
    }
    @Test(arguments: [0, -1, 16]) func rejectsInvalidSoftwareID(_ id: Int) {
        #expect(throws: HidppValidationError.self) { try HidppFrame.build(featureIndex: 5, functionID: 1, softwareID: id) }
    }
    @Test(arguments: [-1, 16]) func rejectsFunctionOverflow(_ function: Int) {
        #expect(throws: HidppValidationError.self) { try HidppFrame.build(featureIndex: 5, functionID: function, softwareID: 9) }
    }
    @Test func rejectsParameterOverflow() {
        #expect(throws: HidppValidationError.tooManyParameters) {
            try HidppFrame.build(featureIndex: 5, functionID: 1, softwareID: 9, parameters: [0, 0, 0, 0])
        }
    }
    @Test func rejectsUnknownAndTruncatedReports() throws {
        #expect(HidppParser.parse([]) == nil)
        #expect(HidppParser.parse(bytes("99 FF 00 09 00 00 00")) == nil)
        #expect(HidppParser.parse(bytes("10 FF 00")) == nil)
        #expect(HidppParser.parse(bytes("11 FF 00 09 01 00 00")) == nil)
    }
    @Test func ignoresTrailingIOHIDBufferPadding() throws {
        let canonical = bytes("10 FF 09 09 03 01 00")
        #expect(HidppParser.parse(canonical + Array(repeating: 0, count: 13))?.bytes == canonical)
    }
    @Test func errorsUseCanonicalLayout() throws {
        let request = try RootFeature.getFeature(0x1814, deviceIndex: 2, softwareID: 9)
        let canonical = try #require(HidppParser.parseError(bytes("10 02 8F 00 09 08 00")))
        #expect(canonical.protocolVersion == 1)
        #expect(canonical.isFor(request))
        let swapped = try #require(HidppParser.parseError(bytes("10 02 8F 09 00 08 00")))
        #expect(!swapped.isFor(request))
        #expect(HidppParser.parseError(bytes("10 02 00 09 05 00 04")) == nil)
    }
    @Test func rootSplitsFeatureAndPreservesReceiverSlot() throws {
        #expect(try RootFeature.getFeature(0x1815, deviceIndex: 3, softwareID: 10).bytes == bytes("10 03 00 0A 18 15 00"))
    }
    @Test func rootLookupMetadataAndUnsupportedZero() throws {
        let lookup = RootFeature.parseLookup(try frame("10 FF 00 0A 05 00 04"))
        #expect(lookup.featureIndex == 5)
        #expect(lookup.version == 4)
        #expect(lookup.isSupported)
        #expect(!RootFeature.parseLookup(try frame("10 FF 00 0A 00 00 00")).isSupported)
    }
    @Test(arguments: [0, 1, 2]) func setCurrentHostGolden(_ host: Int) throws {
        let value = try ChangeHostFeature.setCurrentHost(featureIndex: 12, hostIndex: host, softwareID: 9)
        #expect(value.bytes == [0x10, 0xFF, 0x0C, 0x19, UInt8(host), 0, 0])
    }
    @Test(arguments: [-1, 3, 255]) func rejectsInvalidTargetHost(_ host: Int) {
        #expect(throws: HidppValidationError.self) { try ChangeHostFeature.setCurrentHost(featureIndex: 5, hostIndex: host, softwareID: 9) }
    }
    @Test func zeroFeatureCannotExecuteRootAsChangeHost() {
        #expect(throws: HidppValidationError.unsupportedFeature(0x1814)) {
            try ChangeHostFeature.setCurrentHost(featureIndex: 0, hostIndex: 1, softwareID: 9)
        }
        #expect(throws: HidppValidationError.unsupportedFeature(0x1814)) {
            try ChangeHostFeature.getHostInfo(featureIndex: 0, softwareID: 9)
        }
    }
    @Test func zeroFeatureCannotDecodeRootAsDeparture() throws {
        #expect(ChangeHostFeature.parseHostChangeEvent(try frame("10 FF 00 00 01 02 00"), featureIndex: 0) == nil)
    }
    @Test func getHostInfoGolden() throws {
        #expect(try ChangeHostFeature.getHostInfo(featureIndex: 5, softwareID: 9).bytes == bytes("10 FF 05 09 00 00 00"))
    }
    @Test func nameChunkUsesRemainingAndNullTermination() throws {
        let value = try frame("11 02 03 1A 4D 58 20 4B 45 59 53 20 53 00 00 00 00 00 00 00")
        #expect(DeviceNameFeature.parseNameChunk(value, remaining: 9) == "MX KEYS S")
        #expect(DeviceNameFeature.parseNameChunk(value, remaining: 4) == "MX K")
        #expect(DeviceNameFeature.parseNameChunk(value, remaining: 16) == "MX KEYS S")
        #expect(DeviceNameFeature.parseNameChunk(value, remaining: -1).isEmpty)
    }
    @Test func notificationWritePreservesOtherFlags() throws {
        #expect(try ReceiverMessages.enableWirelessNotifications(registerReply: bytes("10 FF 81 00 92 A8 74")) == bytes("10 FF 80 00 92 A9 74"))
    }
    @Test func errorCannotBecomeReceiverWrite() throws {
        #expect(throws: HidppValidationError.invalidReceiverReply) {
            try ReceiverMessages.enableWirelessNotifications(registerReply: bytes("10 FF 8F 81 00 03 00"))
        }
    }
    @Test(arguments: [
        ("10 03 41 10 41 66 B3", false, 3, 0xB366),
        ("10 03 41 10 01 66 B3", true, 3, 0xB366),
        ("10 02 41 04 52 90 40", false, 2, 0x4090),
        ("10 02 41 04 92 90 40", true, 2, 0x4090)
    ]) func capturedReceiverNotices(_ value: (String, Bool, Int, Int)) throws {
        let notice = try #require(ReceiverMessages.parseDeviceConnection(bytes(value.0)))
        #expect(notice.linkEstablishedAssumed == value.1)
        #expect(notice.deviceIndex == value.2)
        #expect(notice.wirelessProductID == value.3)
    }
    @Test func unrelatedReceiverNoticeRejected() throws {
        #expect(ReceiverMessages.parseDeviceConnection(bytes("10 02 40 10 41 66 B3")) == nil)
        #expect(ReceiverMessages.parseDeviceConnection([0x10, 2, 0x41]) == nil)
        #expect(ReceiverMessages.getShortRegister(0) == bytes("10 FF 81 00 00 00 00"))
    }
    @Test func receiverRegisterEchoMatchesWithoutSoftwareID() throws {
        #expect(HidppMatch.classify(request: ReceiverMessages.getShortRegister(0), response: bytes("10 FF 81 00 00 09 00")) == .response)
    }
}
