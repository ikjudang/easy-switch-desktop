import Foundation
import Testing
@testable import EasySwitchEngine

@Suite("Complete local logs and optional folder copies")
struct FileLogTests {
    @Test(arguments: [false, true]) func frameDiagnosticsAreOptInAndLazy(_ enabled: Bool) async throws {
        let folder = try directory()
        defer { try? FileManager.default.removeItem(at: folder) }
        let log = try FileLog(directory: folder, traceFrames: enabled)
        var formatted = 0
        func frame() -> String { formatted += 1; return "TX fixture-frame" }
        log.writeTrace(frame())
        log.write("기기 연결 해제: fixture")
        await log.flush()
        #expect(formatted == (enabled ? 1 : 0))
        let lines = await log.recentLines()
        #expect(lines.contains { $0.contains("TX fixture-frame") } == enabled)
        #expect(lines.contains { $0.contains("기기 연결 해제") })
    }
    private func directory() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("easyswitch-file-tests-\(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    @Test func disablingFolderCopiesKeepsLocalFileWriting() async throws {
        let log = try FileLog(directory: directory())
        let mirror = try directory()
        log.write("before")
        log.writeCode("ES1|V0213|APP|START")
        try await log.setMirror(directory: mirror)
        log.write("during")
        try await log.setMirror(directory: nil)
        log.write("after")
        await log.flush()
        let local = try String(contentsOf: log.fileURL, encoding: .utf8)
        let external = try String(contentsOf: mirror.appendingPathComponent(log.fileURL.lastPathComponent), encoding: .utf8)
        #expect(local.contains("before") && local.contains("during") && local.contains("after"))
        #expect(external.contains("before") && external.contains("during") && !external.contains("after"))
        #expect(!FileManager.default.fileExists(atPath: mirror.appendingPathComponent(log.codeURL.lastPathComponent).path))
    }
    @Test func exportPreservesEntireFileBeyondVisibleLimitAndSeparatesCodes() async throws {
        let log = try FileLog(directory: directory())
        for index in 0..<620 { log.write("activity \(index)") }
        log.writeCode("ES1|V0213|END|U|LC")
        let output = try directory()
        let details = output.appendingPathComponent("details.txt"), codes = output.appendingPathComponent("codes.txt")
        try await log.export(to: details)
        try await log.export(to: codes, codes: true)
        #expect(await log.recentLines().count == 600)
        #expect(await log.recentCodes().count == 1)
        let complete = try String(contentsOf: details, encoding: .utf8)
        let fixed = try String(contentsOf: codes, encoding: .utf8)
        #expect(complete.contains("activity 0\n") && complete.contains("activity 619\n"))
        #expect(!complete.contains("ES1|") && fixed.contains("|ES1|V0213|END|U|LC"))
    }
    @Test func staleFolderRequestCannotReenableOlderDestination() async throws {
        let log = try FileLog(directory: directory())
        let old = try directory(), newest = try directory()
        try await log.setMirror(directory: newest, revision: 2)
        try await log.setMirror(directory: old, revision: 1)
        log.write("newest only")
        await log.flush()
        #expect(!FileManager.default.fileExists(atPath: old.appendingPathComponent(log.fileURL.lastPathComponent).path))
        #expect(try String(contentsOf: newest.appendingPathComponent(log.fileURL.lastPathComponent), encoding: .utf8).contains("newest only"))
    }
    @Test func atomicCopyReplacesExportAndKeepsLiveFileWritable() async throws {
        let folder = try directory()
        defer { try? FileManager.default.removeItem(at: folder) }
        let log = try FileLog(directory: folder)
        let destination = folder.appendingPathComponent("export.txt")
        try Data("previous export".utf8).write(to: destination)
        log.write("before copy")
        try await log.export(to: destination)
        log.write("after copy")
        try await log.export(to: destination)
        let contents = try String(contentsOf: destination, encoding: .utf8)
        #expect(contents.contains("before copy") && contents.contains("after copy"))
        #expect(!contents.contains("previous export"))
        await #expect(throws: CocoaError.self) { try await log.export(to: log.fileURL) }
        log.write("after rejected self copy")
        await log.flush()
        #expect(try String(contentsOf: log.fileURL, encoding: .utf8).contains("after rejected self copy"))
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).allSatisfy { !$0.hasPrefix(".easyswitch-log-") })
    }
    @Test func failedAtomicReplacementKeepsDestinationAndRemovesStagingFile() async throws {
        let folder = try directory()
        defer { try? FileManager.default.removeItem(at: folder) }
        let log = try FileLog(directory: folder)
        let destination = folder.appendingPathComponent("existing-folder", isDirectory: true)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        let sentinel = destination.appendingPathComponent("keep.txt")
        try Data("keep".utf8).write(to: sentinel)
        log.write("local continues")
        await #expect(throws: POSIXError.self) { try await log.export(to: destination) }
        #expect(try String(contentsOf: sentinel, encoding: .utf8) == "keep")
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).allSatisfy { !$0.hasPrefix(".easyswitch-log-") })
    }
    @Test func localDepartureCodeKeepsDestinationVerdictUnconfirmed() {
        let progress = SwitchProgress(stage: "END", attempt: 2, writes: 1, sends: 2, elapsedMS: 1450,
                                      durationMS: 2, queueMS: 30, writeMS: 5, queryMS: 900, host: nil,
                                      outcome: .localLinkClosed, reason: "LEFT")
        let code = SwitchCode.format(request: 1, source: "B", target: 2, role: "M1", progress: progress)
        #expect(code.contains("|B3|M1|END|U|LC|LEFT|"))
        #expect(code.contains("|Q30|WT5|QT900|A2|N2|RT1|W1|H0"))
    }
    @Test func requestElapsedIncludesPreparationBeforeDeviceWork() {
        let progress = SwitchProgress(stage: "END", attempt: 1, writes: 1, sends: 1, elapsedMS: 1400,
                                      durationMS: 0, queueMS: 1, writeMS: 2, queryMS: 3, host: nil,
                                      outcome: .unverified, reason: "LIMIT")
        let code = SwitchCode.format(request: 3, source: "F", target: 1, role: "M1", progress: progress, requestElapsedMS: 9400)
        #expect(code.contains("|E9400|D1400|"))
    }
}
