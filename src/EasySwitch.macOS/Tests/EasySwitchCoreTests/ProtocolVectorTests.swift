import Foundation
import Testing
@testable import EasySwitchCore

private enum ExpectedValue: Decodable, Sendable {
    case string(String), integer(Int), boolean(Bool), fields([String: Int])
    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let value = try? container.decode(Bool.self) { self = .boolean(value) }
        else if let value = try? container.decode(Int.self) { self = .integer(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else { self = .fields(try container.decode([String: Int].self)) }
    }
    var string: String? { if case .string(let value) = self { value } else { nil } }
    var integer: Int? { if case .integer(let value) = self { value } else { nil } }
    var boolean: Bool? { if case .boolean(let value) = self { value } else { nil } }
    var fields: [String: Int]? { if case .fields(let value) = self { value } else { nil } }
}

private struct ProtocolVector: Decodable, Sendable, CustomTestStringConvertible {
    let id: String
    let kind: String
    let feature: Int?
    let function: Int?
    let device: Int?
    let software: Int?
    let parameters: [UInt8]?
    let report: String?
    let frame: String?
    let request: String?
    let expected: ExpectedValue?
    let expectedError: Bool?
    var testDescription: String { id }
}

private func vectors() throws -> [ProtocolVector] {
    struct Package: Decodable { let packageId: String; let cases: [ProtocolVector] }
    let url = try #require(Bundle.module.url(forResource: "protocol-vectors", withExtension: "json", subdirectory: "Resources"))
    let package = try JSONDecoder().decode(Package.self, from: Data(contentsOf: url))
    #expect(package.packageId == "windows-to-macos-2026-10-24-pc0221-4ab2ff6")
    #expect(package.cases.count == 21)
    return package.cases
}

private func vectorBytes(_ value: String?) throws -> [UInt8] {
    let text = try #require(value)
    return try #require(HidppHex.parse(text))
}

private func vectorFrame(_ value: String?) throws -> HidppFrame {
    try #require(HidppParser.parse(vectorBytes(value)))
}

@Suite("Windows 0.2.21 handoff vectors")
struct ProtocolVectorTests {
    @Test(arguments: try vectors())
    private func matchesReference(_ vector: ProtocolVector) throws {
        switch vector.kind {
        case "build":
            let frame = try HidppFrame.build(featureIndex: #require(vector.feature), functionID: #require(vector.function),
                                              reportType: vector.report == "long" ? .long : .short,
                                              deviceIndex: #require(vector.device), softwareID: #require(vector.software),
                                              parameters: vector.parameters ?? [])
            #expect(HidppHex.format(frame.bytes) == vector.expected?.string)
        case "event":
            let frame = try vectorFrame(vector.frame)
            let feature = try #require(vector.feature)
            #expect(ChangeHostFeature.parseHostChangeEvent(frame, featureIndex: feature) == vector.expected?.integer)
        case "parse":
            let parsed = HidppParser.parse(try vectorBytes(vector.frame))
            #expect((parsed != nil) == vector.expected?.boolean)
        case "host":
            let frame = try vectorFrame(vector.frame)
            if vector.expectedError == true {
                #expect(throws: HidppValidationError.self) { try ChangeHostFeature.parseHostInfo(frame) }
            } else {
                let host = try ChangeHostFeature.parseHostInfo(frame)
                #expect(host.hostCount == vector.expected?.fields?["count"])
                #expect(host.currentHost == vector.expected?.fields?["current"])
            }
        case "match":
            let result = HidppMatch.classify(request: try vectorBytes(vector.request),
                                              response: try vectorBytes(vector.frame))
            #expect(result?.rawValue == vector.expected?.string)
        case "error":
            let error = try #require(HidppParser.parseError(vectorBytes(vector.frame)))
            #expect(error.protocolVersion == vector.expected?.fields?["protocol"])
            #expect(Int(error.failedFeatureIndex) == vector.expected?.fields?["failedFeature"])
            #expect(Int(error.funcSwByte) == vector.expected?.fields?["funcSw"])
            #expect(Int(error.errorCode) == vector.expected?.fields?["code"])
        default:
            Issue.record("Unknown vector kind: \(vector.kind)")
        }
    }
}
