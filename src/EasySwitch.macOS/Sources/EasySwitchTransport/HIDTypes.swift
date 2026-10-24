import Foundation

public struct HIDUsagePair: Sendable, Hashable, Codable {
    public let page: Int
    public let usage: Int

    public init(page: Int, usage: Int) {
        self.page = page
        self.usage = usage
    }
}

public struct HIDDeviceDescriptor: Sendable, Equatable, Identifiable, Codable {
    public let id: String
    /// Groups interfaces of one USB receiver without grouping distinct receivers.
    public let physicalID: String
    public let name: String
    public let vendorID: Int
    public let productID: Int
    public let transport: String
    public let usagePairs: [HIDUsagePair]
    public let maxInputReportSize: Int
    public let maxOutputReportSize: Int
    public let locationID: Int
    public let serialNumber: String?
    public let supportedReportIDs: [Int]
    public let isReceiver: Bool
    public let isHidppCandidate: Bool
    public let isOpen: Bool
    /// Zero means successful open; nil means this non-vendor collection was not opened.
    public let openResult: Int32?

    public init(id: String, physicalID: String, name: String, vendorID: Int, productID: Int,
                transport: String, usagePairs: [HIDUsagePair], maxInputReportSize: Int,
                maxOutputReportSize: Int, locationID: Int, serialNumber: String?,
                supportedReportIDs: [Int], isReceiver: Bool, isHidppCandidate: Bool,
                isOpen: Bool, openResult: Int32?) {
        self.id = id
        self.physicalID = physicalID
        self.name = name
        self.vendorID = vendorID
        self.productID = productID
        self.transport = transport
        self.usagePairs = usagePairs
        self.maxInputReportSize = maxInputReportSize
        self.maxOutputReportSize = maxOutputReportSize
        self.locationID = locationID
        self.serialNumber = serialNumber
        self.supportedReportIDs = supportedReportIDs
        self.isReceiver = isReceiver
        self.isHidppCandidate = isHidppCandidate
        self.isOpen = isOpen
        self.openResult = openResult
    }
}

public enum HIDEvent: Sendable {
    case added(HIDDeviceDescriptor)
    case removed(String)
    /// Bytes are copied in the callback and remain unmodified; normalize at the protocol boundary.
    case report(deviceID: String, reportID: Int, bytes: [UInt8])
    case diagnostic(String)
}

public enum HIDAccessStatus: String, Sendable, Codable {
    case granted, denied, unknown
}

public enum HIDDeviceIdentity {
    /// No product-name fallback: two receivers with unavailable location/serial stay distinct.
    /// The registry-ID fallback is scoped to the current attachment and cannot promise stable selection.
    public static func physicalID(registryID: String, vendorID: Int, productID: Int,
                                  transport: String, locationID: Int, serialNumber: String?) -> String {
        let serial = serialNumber?.trimmingCharacters(in: .whitespacesAndNewlines)
        // Bluetooth's serial identity belongs to the device; a nonzero synthesized location
        // can belong to one attachment. USB location remains the receiver-interface grouping key.
        if transport.localizedCaseInsensitiveContains("bluetooth"), let serial, !serial.isEmpty {
            return "\(transport):\(vendorID):\(productID):\(serial)"
        }
        if locationID != 0 {
            return "\(transport):\(vendorID):\(productID):\(locationID)"
        }
        if let serial, !serial.isEmpty {
            return "\(transport):\(vendorID):\(productID):\(serial)"
        }
        return registryID
    }
}

public enum HIDTransportError: Error, Sendable, CustomStringConvertible {
    case alreadyRunning
    case stopped
    case managerOpen(Int32)
    case deviceUnavailable(String)
    case invalidReport(String)
    case writeFailed(Int32)

    public var description: String {
        switch self {
        case .alreadyRunning: "HID monitor is already running"
        case .stopped: "HID monitor is stopped"
        case .managerOpen(let code): String(format: "IOHIDManagerOpen failed: 0x%08X", UInt32(bitPattern: code))
        case .deviceUnavailable(let id): "HID device is unavailable: \(id)"
        case .invalidReport(let reason): "Invalid HID++ report: \(reason)"
        case .writeFailed(let code): String(format: "IOHIDDeviceSetReport failed: 0x%08X", UInt32(bitPattern: code))
        }
    }
}

public enum HIDReportNormalizer {
    /// HID++ canonical bytes include exactly one report ID. A callback may include or omit it.
    /// The observed callback ID and length, rather than a guessed Bluetooth offset, select the form.
    public static func normalize(reportID: Int, bytes: [UInt8]) throws -> [UInt8] {
        guard reportID == 0x10 || reportID == 0x11 else {
            throw HIDTransportError.invalidReport("unsupported callback report ID \(reportID)")
        }
        let length = reportID == 0x10 ? 7 : 20
        if bytes.count >= length, bytes.first == UInt8(reportID) {
            return Array(bytes.prefix(length))
        }
        if bytes.count == length - 1 {
            return [UInt8(reportID)] + bytes
        }
        throw HIDTransportError.invalidReport("callback ID=\(reportID), length=\(bytes.count), first=\(bytes.first.map(String.init) ?? "none")")
    }

    /// Parses HID short/long items without treating payload bytes as report-ID declarations.
    public static func reportIDs(in descriptor: [UInt8]) -> [Int] {
        var ids = Set<Int>()
        var offset = 0
        while offset < descriptor.count {
            let prefix = descriptor[offset]
            if prefix == 0xFE {
                guard offset + 2 < descriptor.count else { break }
                let length = Int(descriptor[offset + 1])
                guard offset + 3 + length <= descriptor.count else { break }
                offset += 3 + length
                continue
            }
            let sizeBits = Int(prefix & 3)
            let length = sizeBits == 3 ? 4 : sizeBits
            guard offset + 1 + length <= descriptor.count else { break }
            if prefix == 0x85, length == 1 { ids.insert(Int(descriptor[offset + 1])) }
            offset += 1 + length
        }
        return ids.sorted()
    }

    public static func isHidppCandidate(vendorID: Int, usagePairs: [HIDUsagePair]) -> Bool {
        vendorID == 0x046D && usagePairs.contains {
            ($0.page == 0xFF43 && $0.usage == 0x0202) ||
            ($0.page == 0xFF00 && ($0.usage == 1 || $0.usage == 2))
        }
    }
}
