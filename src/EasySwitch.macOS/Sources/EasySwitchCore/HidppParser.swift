import Foundation

public struct HidppErrorInfo: Equatable, Sendable, Error {
    public let protocolVersion: Int
    public let failedFeatureIndex: UInt8
    public let funcSwByte: UInt8
    public let errorCode: UInt8

    public init(protocolVersion: Int, failedFeatureIndex: UInt8, funcSwByte: UInt8, errorCode: UInt8) {
        self.protocolVersion = protocolVersion
        self.failedFeatureIndex = failedFeatureIndex
        self.funcSwByte = funcSwByte
        self.errorCode = errorCode
    }

    public func isFor(_ request: HidppFrame) -> Bool {
        failedFeatureIndex == request.featureIndex && funcSwByte == request.funcSwByte
    }
}

public enum HidppParser {
    /// Accept a fixed-size IOHID buffer with trailing padding, but never fill a truncated frame.
    public static func parse(_ raw: [UInt8]) -> HidppFrame? {
        guard let reportID = raw.first, let type = HidppReportType(rawValue: reportID), raw.count >= type.totalLength else { return nil }
        return try? HidppFrame(reportType: type, deviceIndex: raw[1], featureIndex: raw[2], funcSwByte: raw[3],
                               parameters: Array(raw[4..<type.totalLength]))
    }

    public static func isError(_ raw: [UInt8]) -> Bool {
        guard raw.count >= 7, raw[0] == 0x10 || raw[0] == 0x11 else { return false }
        return raw[2] == 0xFF || raw[2] == 0x8F
    }

    /// Canonical error layout is [report,device,FF/8F,failedFeature,funcSw,error,...].
    public static func parseError(_ raw: [UInt8]) -> HidppErrorInfo? {
        guard isError(raw) else { return nil }
        return HidppErrorInfo(protocolVersion: raw[2] == 0xFF ? 2 : 1,
                              failedFeatureIndex: raw[3], funcSwByte: raw[4], errorCode: raw[5])
    }
}

public enum HidppResponseKind: String, Equatable, Sendable {
    case response = "Response"
    case errorCanonical = "ErrorSolaarLayout"
    case errorSwapped = "ErrorSwappedLayout"
}

public enum HidppMatch {
    /// Same slot, feature and function/software byte are required. Short requests may receive long replies.
    /// Keep both historical error layouts distinct so the caller can log which layout was actually observed.
    public static func classify(request: [UInt8], response raw: [UInt8]) -> HidppResponseKind? {
        guard request.count >= 4, raw.count >= 7, raw[1] == request[1] else { return nil }
        if raw[2] == request[2], raw[3] == request[3] { return .response }
        if raw[2] == 0xFF || raw[2] == 0x8F {
            if raw[3] == request[2], raw[4] == request[3] { return .errorCanonical }
            if raw[3] == request[3], raw[4] == request[2] { return .errorSwapped }
        }
        return nil
    }
}
