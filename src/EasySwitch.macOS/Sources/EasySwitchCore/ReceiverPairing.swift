import Foundation

public struct ReceiverPairingProgress: Codable, Sendable, Equatable {
    public var receiverID: String
    public var stage: String
    public var deviceName: String = ""
    public var instruction: String = ""
    public var instructionEnglish: String = ""
    public var slot: Int?
    public init(receiverID: String, stage: String) { self.receiverID = receiverID; self.stage = stage }
}

/// Receiver registers are HID++ 1.0, independent of per-device 0x1814 feature indices.
public struct ReceiverPairing: Sendable {
    public private(set) var progress: ReceiverPairingProgress
    private let bolt: Bool
    private let knownSlots: Set<UInt8>
    private var counter: Int?
    private var address: [UInt8]?
    private var authentication: UInt8?
    private var kind: UInt8?
    public init(receiverID: String, bolt: Bool, knownSlots: Set<UInt8> = []) {
        progress = .init(receiverID: receiverID, stage: "searching"); self.bolt = bolt; self.knownSlots = knownSlots
    }
    public static func register(_ number: UInt8, parameters: [UInt8], read: Bool = false) throws -> HidppFrame {
        guard parameters.count <= 16 else { throw HidppValidationError.tooManyParameters }
        let long = parameters.count > 3 || number == 0xC1 || number == 0xB5
        return try HidppFrame(reportType: long ? .long : .short, deviceIndex: 0xFF,
            featureIndex: read ? (long ? 0x83 : 0x81) : (long ? 0x82 : 0x80), funcSwByte: number,
            parameters: parameters + Array(repeating: 0, count: (long ? 16 : 3) - parameters.count))
    }
    public func search() throws -> HidppFrame {
        try Self.register(bolt ? 0xC0 : 0xB2, parameters: bolt ? [30, 1] : [1, 0, 30])
    }
    public func cancel() throws -> HidppFrame {
        if bolt, progress.stage == "authenticating" { return try Self.register(0xC1, parameters: [2]) }
        return try Self.register(bolt ? 0xC0 : 0xB2, parameters: bolt ? [0, 2] : [2, 0, 0])
    }
    public mutating func confirm() throws -> HidppFrame {
        guard bolt, progress.stage == "candidate", let address, let authentication, let kind else {
            throw HidppValidationError.invalidField("pairing candidate")
        }
        progress.stage = "authenticating"
        return try Self.register(0xC1, parameters: [1, 0] + address + [authentication, kind == 1 ? 20 : 10])
    }
    public mutating func accept(_ frame: HidppFrame) {
        if !bolt {
            if frame.featureIndex == 0x4A, frame.deviceIndex == 0xFF {
                if frame.param(0) != 0 { progress.stage = "failed" }
                else if frame.funcSwByte & 1 == 0, progress.slot == nil { progress.stage = "closed" }
            } else if frame.featureIndex == 0x41, (1...6).contains(frame.deviceIndex), !knownSlots.contains(frame.deviceIndex) {
                progress.stage = "complete"; progress.slot = Int(frame.deviceIndex)
            }
            return
        }
        guard frame.deviceIndex == 0xFF else { return }
        switch frame.featureIndex {
        case 0x4F:
            guard progress.stage == "searching" || progress.stage == "candidate" else { return }
            let value = Int(frame.funcSwByte) + Int(frame.param(0)) * 256
            if let counter, counter != value { return }
            counter = value
            if frame.param(1) == 0, frame.parameters.count >= 15 {
                kind = frame.param(3); address = Array(frame.parameters[6..<12]); authentication = frame.param(14)
            } else if frame.param(1) == 1 {
                progress.deviceName = String(decoding: frame.parameters.dropFirst(3).prefix(Int(frame.param(2))), as: UTF8.self)
            }
            if address != nil, authentication != nil, !progress.deviceName.isEmpty { progress.stage = "candidate" }
        case 0x4D:
            let passkey = String(decoding: frame.parameters.prefix(6), as: UTF8.self)
            guard passkey.count == 6, passkey.allSatisfy(\.isNumber) else { return }
            progress.stage = "authenticating"
            if (authentication ?? 0) & 1 != 0 {
                progress.instruction = "키보드에서 \(passkey)를 입력하고 Enter를 눌러 줘."
                progress.instructionEnglish = "Type \(passkey) on the keyboard, then press Enter."
            }
            else if let value = Int(passkey), value < 1024 {
                progress.instruction = (0..<10).reversed().map { value & (1 << $0) == 0 ? "왼쪽" : "오른쪽" }.joined(separator: " → ")
                    + " 순서로 마우스 버튼을 누른 뒤 양쪽 버튼을 함께 눌러 줘."
                progress.instructionEnglish = (0..<10).reversed().map { value & (1 << $0) == 0 ? "Left" : "Right" }.joined(separator: " → ")
                    + ". Then press both mouse buttons together."
            }
        case 0x53:
            if frame.param(0) != 0 { progress.stage = "failed" }
        case 0x54:
            if frame.param(0) != 0 { progress.stage = "failed" }
            else if frame.funcSwByte == 2, (1...6).contains(frame.param(7)) {
                progress.stage = "complete"; progress.slot = Int(frame.param(7))
            }
        default: break
        }
    }
}
