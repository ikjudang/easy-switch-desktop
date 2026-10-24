import Foundation

/// Canonical HID++ frames include the report ID. Transport framing belongs at the IOHID boundary.
public enum HidppReportType: UInt8, Sendable, Codable, CaseIterable {
    case short = 0x10
    case long = 0x11

    public var totalLength: Int { self == .short ? 7 : 20 }
    public var parameterLength: Int { totalLength - 4 }
}

public enum HidppValidationError: Error, Equatable, LocalizedError, Sendable {
    case invalidField(String)
    case tooManyParameters
    case invalidParameterLength
    case unsupportedFeature(Int)
    case invalidHostInfo(count: Int, current: Int)
    case invalidReceiverReply

    public var errorDescription: String? {
        switch self {
        case .invalidField(let name): return "HID++ field out of range: \(name)"
        case .tooManyParameters: return "HID++ parameters exceed report capacity"
        case .invalidParameterLength: return "HID++ parameter length does not match report type"
        case .unsupportedFeature(let feature): return String(format: "Feature 0x%04X is unsupported (index 0)", feature)
        case .invalidHostInfo(let count, let current): return "Invalid host information: count=\(count), current=\(current)"
        case .invalidReceiverReply: return "Not a successful receiver notification register reply"
        }
    }
}

public struct HidppFrame: Equatable, Hashable, Sendable {
    public let reportType: HidppReportType
    public let deviceIndex: UInt8
    public let featureIndex: UInt8
    public let funcSwByte: UInt8
    public let parameters: [UInt8]

    public init(reportType: HidppReportType, deviceIndex: UInt8, featureIndex: UInt8,
                funcSwByte: UInt8, parameters: [UInt8]) throws {
        guard parameters.count == reportType.parameterLength else {
            throw HidppValidationError.invalidParameterLength
        }
        self.reportType = reportType
        self.deviceIndex = deviceIndex
        self.featureIndex = featureIndex
        self.funcSwByte = funcSwByte
        self.parameters = parameters
    }

    /// softwareID is mandatory: the application uses 0x09; the reference tests also exercise 0x0A.
    public static func build(featureIndex: Int, functionID: Int,
                             reportType: HidppReportType = .short, deviceIndex: Int = 0xFF,
                             softwareID: Int, parameters: [UInt8] = []) throws -> HidppFrame {
        guard (0...255).contains(featureIndex) else { throw HidppValidationError.invalidField("featureIndex") }
        guard (0...255).contains(deviceIndex) else { throw HidppValidationError.invalidField("deviceIndex") }
        guard (0...15).contains(functionID) else { throw HidppValidationError.invalidField("functionID") }
        guard (1...15).contains(softwareID) else { throw HidppValidationError.invalidField("softwareID") }
        guard parameters.count <= reportType.parameterLength else { throw HidppValidationError.tooManyParameters }
        return try HidppFrame(reportType: reportType, deviceIndex: UInt8(deviceIndex), featureIndex: UInt8(featureIndex),
                              funcSwByte: UInt8(functionID << 4 | softwareID),
                              parameters: parameters + Array(repeating: 0, count: reportType.parameterLength - parameters.count))
    }

    public var functionID: UInt8 { funcSwByte >> 4 }
    public var softwareID: UInt8 { funcSwByte & 0x0F }
    public var bytes: [UInt8] { [reportType.rawValue, deviceIndex, featureIndex, funcSwByte] + parameters }
    public func param(_ index: Int) -> UInt8 { parameters.indices.contains(index) ? parameters[index] : 0 }

    public func toLong() -> HidppFrame {
        guard reportType != .long else { return self }
        // The initializer cannot fail: .short has 3 params and .long has 16.
        return try! HidppFrame(reportType: .long, deviceIndex: deviceIndex, featureIndex: featureIndex,
                               funcSwByte: funcSwByte, parameters: parameters + Array(repeating: 0, count: 13))
    }

    public func echoes(deviceIndex: UInt8, featureIndex: UInt8, funcSwByte: UInt8) -> Bool {
        self.deviceIndex == deviceIndex && self.featureIndex == featureIndex && self.funcSwByte == funcSwByte
    }
}

public enum HidppHex {
    public static func format(_ bytes: [UInt8]) -> String { bytes.map { String(format: "%02X", $0) }.joined(separator: " ") }
    public static func parse(_ value: String) -> [UInt8]? {
        var bytes: [UInt8] = []
        for token in value.split(whereSeparator: \.isWhitespace) {
            guard let byte = UInt8(token, radix: 16) else { return nil }
            bytes.append(byte)
        }
        return bytes
    }
}
