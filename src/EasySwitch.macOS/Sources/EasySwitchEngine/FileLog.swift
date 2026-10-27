import Foundation
import Darwin

/// Owns synchronous file I/O on a private queue, never the AppKit thread.
public final class FileLog: @unchecked Sendable {
    public let directory: URL
    public let fileURL: URL
    public let codeURL: URL
    public let traceFrames: Bool
    private let queue = DispatchQueue(label: "EasySwitch.file-log")
    private let formatter = ISO8601DateFormatter()
    private let codeFormatter = DateFormatter()
    private var handle: FileHandle?
    private var codeHandle: FileHandle?
    private var codeLines: [String] = []
    private var mirror: FileHandle?
    private var mirrorRevision = 0
    private var lines: [String] = []
    private var failure: String?

    public init(directory: URL? = nil, traceFrames: Bool = false) throws {
        self.traceFrames = traceFrames
        self.directory = directory ?? FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Logs/EasySwitch", isDirectory: true)
        try FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true)
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        formatter.timeZone = .current
        codeFormatter.locale = Locale(identifier: "en_US_POSIX")
        codeFormatter.timeZone = .current
        codeFormatter.dateFormat = "yyyyMMdd'T'HHmmss.SSSZZZZZ"
        let name = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
        fileURL = self.directory.appendingPathComponent("easyswitch-macos-\(name)-\(ProcessInfo.processInfo.processIdentifier).txt")
        codeURL = fileURL.deletingPathExtension().appendingPathExtension("codes.txt")
        guard FileManager.default.createFile(atPath: fileURL.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown)
        }
        handle = try FileHandle(forWritingTo: fileURL)
        guard FileManager.default.createFile(atPath: codeURL.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
        codeHandle = try FileHandle(forWritingTo: codeURL)
    }

    public func write(_ message: String) {
        queue.async { [self] in
            let line = "\(formatter.string(from: Date())) \(message)"
            lines.append(line)
            if lines.count > 600 { lines.removeFirst(lines.count - 600) }
            do { try handle?.write(contentsOf: Data((line + "\n").utf8)) }
            catch {
                failure = "로컬 로그 기록 실패: \(error.localizedDescription)"
                FileHandle.standardError.write(Data((failure! + "\n").utf8))
            }
            do { try mirror?.write(contentsOf: Data((line + "\n").utf8)) }
            catch { failure = "외부 폴더 로그 기록 실패: \(error.localizedDescription)"; try? mirror?.close(); mirror = nil }
        }
    }

    /// Diagnostic frames are opt-in. Do not format/copy their payloads in normal operation.
    public func writeTrace(_ message: @autoclosure () -> String) {
        guard traceFrames else { return }
        write(message())
    }

    public func writeCode(_ message: String) {
        queue.async { [self] in
            let line = "\(codeFormatter.string(from: Date()))|\(message)"
            codeLines.append(line)
            if codeLines.count > 600 { codeLines.removeFirst(codeLines.count - 600) }
            do { try codeHandle?.write(contentsOf: Data((line + "\n").utf8)) }
            catch { failure = "코드 로그 기록 실패: \(error.localizedDescription)" }
        }
    }

    public func setMirror(directory: URL?, revision: Int = 0) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            queue.async { [self] in
                do {
                    if revision != 0 && revision < mirrorRevision { continuation.resume(); return }
                    mirrorRevision = max(revision, mirrorRevision)
                    try mirror?.close(); mirror = nil
                    if let directory {
                        for source in [fileURL] {
                            let destination = directory.appendingPathComponent(source.lastPathComponent)
                            guard destination.standardizedFileURL != source.standardizedFileURL else { throw CocoaError(.fileWriteInvalidFileName) }
                            try copyAtomically(source, to: destination)
                            let sink = try FileHandle(forWritingTo: destination)
                            try sink.seekToEnd()
                            mirror = sink
                        }
                    }
                    continuation.resume()
                } catch { try? mirror?.close(); mirror = nil; continuation.resume(throwing: error) }
            }
        }
    }

    public func recentCodes() async -> [String] {
        await withCheckedContinuation { continuation in queue.async { [self] in continuation.resume(returning: codeLines) } }
    }

    public func recentLines() async -> [String] {
        await withCheckedContinuation { continuation in
            queue.async { [self] in
                continuation.resume(returning: failure.map { lines + [$0] } ?? lines)
            }
        }
    }

    public func flush() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            queue.async { [self] in
                do { try handle?.synchronize(); try codeHandle?.synchronize(); try mirror?.synchronize() }
                catch { FileHandle.standardError.write(Data("로그 flush 실패: \(error)\n".utf8)) }
                continuation.resume()
            }
        }
    }

    public func export(to destination: URL, codes: Bool = false) async throws {
        await flush()
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            queue.async { [self] in
                do {
                    // The save panel owns overwrite confirmation; copying also preserves the live log.
                    try copyAtomically(codes ? codeURL : fileURL, to: destination)
                    continuation.resume()
                } catch { continuation.resume(throwing: error) }
            }
        }
    }

    /// copyfile-backed copying bounds the userspace buffer regardless of the log's size.
    /// Stage beside the destination, then atomically replace it; a failed copy keeps the old file.
    private func copyAtomically(_ source: URL, to destination: URL) throws {
        guard source.standardizedFileURL != destination.standardizedFileURL else { throw CocoaError(.fileWriteInvalidFileName) }
        let temporary = destination.deletingLastPathComponent().appendingPathComponent(".easyswitch-log-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: temporary) }
        try FileManager.default.copyItem(at: source, to: temporary)
        guard rename(temporary.path, destination.path) == 0 else {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
    }

    deinit { try? handle?.close(); try? codeHandle?.close(); try? mirror?.close() }
}
