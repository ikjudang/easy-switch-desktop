import Foundation

public struct FeatureLookup: Equatable, Sendable {
    public let featureIndex: Int
    public let typeFlags: Int
    public let version: Int
    public var isSupported: Bool { featureIndex != 0 }
}

public enum RootFeature {
    public static func getFeature(_ featureID: Int, deviceIndex: Int = 0xFF, softwareID: Int) throws -> HidppFrame {
        guard (0...0xFFFF).contains(featureID) else { throw HidppValidationError.invalidField("featureID") }
        return try HidppFrame.build(featureIndex: 0, functionID: 0, deviceIndex: deviceIndex, softwareID: softwareID,
                                     parameters: [UInt8(featureID >> 8), UInt8(featureID & 0xFF)])
    }

    public static func parseLookup(_ frame: HidppFrame) -> FeatureLookup {
        FeatureLookup(featureIndex: Int(frame.param(0)), typeFlags: Int(frame.param(1)), version: Int(frame.param(2)))
    }
}

public struct HostInfo: Equatable, Sendable {
    public let hostCount: Int
    public let currentHost: Int

    public init(hostCount: Int, currentHost: Int) {
        self.hostCount = hostCount
        self.currentHost = currentHost
    }
}

public enum ChangeHostFeature {
    public static let featureID = 0x1814

    private static func validateFeature(_ index: Int) throws {
        guard index != 0 else { throw HidppValidationError.unsupportedFeature(featureID) }
        guard (1...255).contains(index) else { throw HidppValidationError.invalidField("featureIndex") }
    }

    public static func getHostInfo(featureIndex: Int, deviceIndex: Int = 0xFF, softwareID: Int) throws -> HidppFrame {
        try validateFeature(featureIndex)
        return try HidppFrame.build(featureIndex: featureIndex, functionID: 0, deviceIndex: deviceIndex, softwareID: softwareID)
    }

    /// Send this without awaiting a HID++ response: switching may immediately tear down the link.
    public static func setCurrentHost(featureIndex: Int, hostIndex: Int, deviceIndex: Int = 0xFF, softwareID: Int) throws -> HidppFrame {
        try validateFeature(featureIndex)
        guard (0...2).contains(hostIndex) else { throw HidppValidationError.invalidField("hostIndex") }
        return try HidppFrame.build(featureIndex: featureIndex, functionID: 1, deviceIndex: deviceIndex,
                                     softwareID: softwareID, parameters: [UInt8(hostIndex)])
    }

    public static func parseHostInfo(_ frame: HidppFrame) throws -> HostInfo {
        let count = Int(frame.param(0)), current = Int(frame.param(1))
        guard (1...3).contains(count), current < count else {
            throw HidppValidationError.invalidHostInfo(count: count, current: current)
        }
        return HostInfo(hostCount: count, currentHost: current)
    }

    /// params[0] is the source host, not a validity flag; params[1] alone holds the destination.
    public static func parseHostChangeEvent(_ frame: HidppFrame, featureIndex: Int) -> Int? {
        guard (1...255).contains(featureIndex), Int(frame.featureIndex) == featureIndex, frame.funcSwByte == 0 else { return nil }
        let target = Int(frame.param(1))
        return (0...2).contains(target) ? target : nil
    }
}

public enum DeviceNameFeature {
    public static let featureID = 0x0005
    public static func getCount(featureIndex: Int, deviceIndex: Int, softwareID: Int) throws -> HidppFrame {
        try HidppFrame.build(featureIndex: featureIndex, functionID: 0, deviceIndex: deviceIndex, softwareID: softwareID)
    }
    public static func getName(featureIndex: Int, characterIndex: Int, deviceIndex: Int, softwareID: Int) throws -> HidppFrame {
        guard (0...255).contains(characterIndex) else { throw HidppValidationError.invalidField("characterIndex") }
        return try HidppFrame.build(featureIndex: featureIndex, functionID: 1, deviceIndex: deviceIndex,
                                     softwareID: softwareID, parameters: [UInt8(characterIndex)])
    }
    public static func parseNameChunk(_ frame: HidppFrame, remaining: Int) -> String {
        let prefix = frame.parameters.prefix(max(0, remaining)).prefix(while: { $0 != 0 })
        return String(decoding: prefix, as: UTF8.self)
    }
}

public struct DeviceConnectionNotice: Equatable, Sendable {
    public let deviceIndex: Int
    public let protocolVersion: Int
    public let flags: UInt8
    public let wirelessProductID: Int
    /// Receiver bit 6 reports the absence of a local link. Preserve the raw flags for platform measurements.
    public var linkEstablishedAssumed: Bool { flags & 0x40 == 0 }
}

public enum ReceiverMessages {
    public static func getShortRegister(_ address: UInt8) -> [UInt8] { [0x10, 0xFF, 0x81, address, 0, 0, 0] }

    /// Build only; the caller must explicitly authorize configuration writes and verify the result.
    public static func enableWirelessNotifications(registerReply: [UInt8]) throws -> [UInt8] {
        guard registerReply.count >= 7, registerReply[1] == 0xFF, registerReply[2] == 0x81, registerReply[3] == 0 else {
            throw HidppValidationError.invalidReceiverReply
        }
        return [0x10, 0xFF, 0x80, 0, registerReply[4], registerReply[5] | 1, registerReply[6]]
    }

    public static func parseDeviceConnection(_ raw: [UInt8]) -> DeviceConnectionNotice? {
        guard raw.count >= 7, raw[0] == 0x10 || raw[0] == 0x11, raw[2] == 0x41 else { return nil }
        return DeviceConnectionNotice(deviceIndex: Int(raw[1]), protocolVersion: Int(raw[3]), flags: raw[4],
                                       wirelessProductID: Int(raw[5]) | Int(raw[6]) << 8)
    }
}
