import XCTest
@testable import EasySwitchTransport

final class HIDReportNormalizerTests: XCTestCase {
    func testCanonicalNumberedReportIsNotDuplicated() throws {
        let bytes: [UInt8] = [0x10, 0xFF, 0, 9, 0x18, 0x14, 0]
        XCTAssertEqual(try HIDReportNormalizer.normalize(reportID: 0x10, bytes: bytes), bytes)
    }

    func testPayloadWithoutIDIsPrefixedExactlyOnce() throws {
        let bytes: [UInt8] = [0xFF, 0, 9, 0x18, 0x14, 0]
        XCTAssertEqual(try HIDReportNormalizer.normalize(reportID: 0x10, bytes: bytes), [0x10] + bytes)
    }

    func testBluetoothPayloadKeepsDeviceIndex() throws {
        let payload = [UInt8(0xFF), 9, 0, 1, 2] + Array(repeating: UInt8(0), count: 14)
        let normalized = try HIDReportNormalizer.normalize(reportID: 0x11, bytes: payload)
        XCTAssertEqual(normalized.count, 20)
        XCTAssertEqual(Array(normalized.prefix(6)), [0x11, 0xFF, 9, 0, 1, 2])
    }

    func testPaddedShortRetainsOnlyItsCanonicalLength() throws {
        let short: [UInt8] = [0x10, 1, 9, 0, 1, 2, 0]
        XCTAssertEqual(try HIDReportNormalizer.normalize(reportID: 0x10, bytes: short + Array(repeating: 0, count: 13)), short)
    }

    func testMalformedAndUnknownReportsAreRejected() {
        XCTAssertThrowsError(try HIDReportNormalizer.normalize(reportID: 0x11, bytes: [0x11, 1, 2]))
        XCTAssertThrowsError(try HIDReportNormalizer.normalize(reportID: 0x10, bytes: [0x11, 1, 2, 3, 4, 5, 6]))
        XCTAssertThrowsError(try HIDReportNormalizer.normalize(reportID: 3, bytes: Array(repeating: 0, count: 7)))
    }

    func testDescriptorParserSkipsPayloadBytesAndLongItems() {
        let descriptor: [UInt8] = [0x06, 0x85, 0xFF, 0x85, 0x10, 0xFE, 2, 0, 0x85, 0x77, 0x85, 0x11, 0x85, 0x10]
        XCTAssertEqual(HIDReportNormalizer.reportIDs(in: descriptor), [0x10, 0x11])
        XCTAssertEqual(HIDReportNormalizer.reportIDs(in: [0x85]), [])
        XCTAssertEqual(HIDReportNormalizer.reportIDs(in: [0xFE, 100, 0]), [])
    }

    func testOnlyVendorCollectionIsCandidate() {
        XCTAssertTrue(HIDReportNormalizer.isHidppCandidate(vendorID: 0x046D, usagePairs: [.init(page: 0xFF43, usage: 0x0202)]))
        XCTAssertTrue(HIDReportNormalizer.isHidppCandidate(vendorID: 0x046D, usagePairs: [.init(page: 0xFF00, usage: 2)]))
        XCTAssertFalse(HIDReportNormalizer.isHidppCandidate(vendorID: 0x046D, usagePairs: [.init(page: 1, usage: 6)]))
        XCTAssertFalse(HIDReportNormalizer.isHidppCandidate(vendorID: 0x1234, usagePairs: [.init(page: 0xFF00, usage: 2)]))
    }

    func testMissingPhysicalIdentifiersNeverMergeDifferentReceivers() {
        func identity(_ registry: String, _ serial: String?) -> String {
            HIDDeviceIdentity.physicalID(registryID: registry, vendorID: 0x046D, productID: 0xC548,
                                         transport: "USB", locationID: 0, serialNumber: serial)
        }
        XCTAssertEqual(identity("iohid:1", nil), "iohid:1")
        XCTAssertEqual(identity("iohid:2", ""), "iohid:2")
        XCTAssertEqual(identity("iohid:3", "   "), "iohid:3")
        XCTAssertNotEqual(identity("iohid:1", nil), identity("iohid:2", nil))
    }

    func testSiblingCollectionsGroupByLocationWithoutMergingOtherReceivers() {
        func identity(_ registry: String, _ location: Int) -> String {
            HIDDeviceIdentity.physicalID(registryID: registry, vendorID: 0x046D, productID: 0xC548,
                                         transport: "USB", locationID: location, serialNumber: nil)
        }
        XCTAssertEqual(identity("iohid:1", 123), identity("iohid:2", 123))
        XCTAssertNotEqual(identity("iohid:1", 123), identity("iohid:3", 456))
    }

    func testBluetoothSerialPreservesIdentityAcrossRegistryChanges() {
        func identity(_ registry: String, _ serial: String) -> String {
            HIDDeviceIdentity.physicalID(registryID: registry, vendorID: 0x046D, productID: 0xB366,
                                         transport: "Bluetooth Low Energy", locationID: 0, serialNumber: serial)
        }
        XCTAssertEqual(identity("iohid:1", "keyboard-A"), identity("iohid:9", "keyboard-A"))
        XCTAssertNotEqual(identity("iohid:1", "keyboard-A"), identity("iohid:2", "keyboard-B"))
    }

    func testBluetoothSerialTakesPriorityOverChangingLocation() {
        func identity(_ location: Int, _ serial: String?) -> String {
            HIDDeviceIdentity.physicalID(registryID: "iohid:\(location)", vendorID: 0x046D, productID: 0xB366,
                                         transport: "Bluetooth Low Energy", locationID: location, serialNumber: serial)
        }
        XCTAssertEqual(identity(123, "keyboard-A"), identity(456, "keyboard-A"))
        XCTAssertNotEqual(identity(123, "keyboard-A"), identity(123, "keyboard-B"))
        XCTAssertNotEqual(identity(123, nil), identity(456, nil))
    }

    func testUSBLocationRemainsPrimaryForReceiverSiblingGrouping() {
        let left = HIDDeviceIdentity.physicalID(registryID: "iohid:1", vendorID: 0x046D, productID: 0xC548,
                                                transport: "USB", locationID: 123, serialNumber: "interface-A")
        let right = HIDDeviceIdentity.physicalID(registryID: "iohid:2", vendorID: 0x046D, productID: 0xC548,
                                                 transport: "USB", locationID: 123, serialNumber: "interface-B")
        XCTAssertEqual(left, right)
    }

    func testPermissionDenialDoesNotIncludeOtherOpenFailures() {
        XCTAssertTrue(HIDTransportError.isPermissionDenied(Int32(bitPattern: 0xE00002E2)))
        XCTAssertTrue(HIDTransportError.isPermissionDenied(Int32(bitPattern: 0xE00002C1)))
        XCTAssertFalse(HIDTransportError.isPermissionDenied(Int32(bitPattern: 0xE00002C5)))
        XCTAssertFalse(HIDTransportError.isPermissionDenied(0))
    }
}
