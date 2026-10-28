import Foundation

/// Bounded, cancellable process execution shared by engine and Git operations.
/// Standard output and error are drained concurrently to prevent pipe deadlocks.
struct ProcessExecutionResult: Sendable {
    let standardOutput: Data
    let standardError: Data
    let terminationStatus: Int32
}

enum ProcessExecutionError: LocalizedError {
    case outputLimitExceeded(stream: String, limit: Int)

    var errorDescription: String? {
        switch self {
        case .outputLimitExceeded(let stream, let limit):
            return "El proceso generó demasiada salida en \(stream) (límite: \(limit) bytes)."
        }
    }
}

private final class ProcessTerminationController: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var cancellationRequested = false

    var isCancellationRequested: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancellationRequested
    }

    func install(_ process: Process) {
        lock.lock()
        self.process = process
        let shouldTerminate = cancellationRequested
        lock.unlock()
        if shouldTerminate, process.isRunning { process.terminate() }
    }

    func didLaunch() {
        lock.lock()
        let process = self.process
        let shouldTerminate = cancellationRequested
        lock.unlock()
        if shouldTerminate, let process, process.isRunning { process.terminate() }
    }

    func cancel() {
        lock.lock()
        cancellationRequested = true
        let process = self.process
        lock.unlock()
        if let process, process.isRunning { process.terminate() }
    }
}

private struct BoundedPipeOutput: Sendable {
    let data: Data
    let exceededLimit: Bool
}

private enum PipeOutputReader {
    static func drain(_ handle: FileHandle, limit: Int) -> BoundedPipeOutput {
        var data = Data()
        var exceeded = false
        while true {
            let next: Data?
            do { next = try handle.read(upToCount: 64 * 1024) } catch { break }
            guard let chunk = next, !chunk.isEmpty else { break }
            let remaining = max(0, limit - data.count)
            if remaining > 0 { data.append(chunk.prefix(remaining)) }
            if chunk.count > remaining { exceeded = true }
        }
        try? handle.close()
        return BoundedPipeOutput(data: data, exceededLimit: exceeded)
    }
}

enum ProcessExecutionService {
    static func run(
        executableURL: URL,
        arguments: [String],
        currentDirectoryURL: URL? = nil,
        environment: [String: String]? = nil,
        maximumStandardOutputBytes: Int = 128 * 1024 * 1024,
        maximumStandardErrorBytes: Int = 4 * 1024 * 1024
    ) async throws -> ProcessExecutionResult {
        let controller = ProcessTerminationController()
        return try await withTaskCancellationHandler {
            try await Task.detached(priority: .utility) {
                let process = Process()
                process.executableURL = executableURL
                process.arguments = arguments
                process.currentDirectoryURL = currentDirectoryURL
                if let environment {
                    process.environment = ProcessInfo.processInfo.environment.merging(environment) { _, new in new }
                }

                let stdout = Pipe()
                let stderr = Pipe()
                process.standardOutput = stdout
                process.standardError = stderr
                controller.install(process)
                do {
                    try process.run()
                } catch {
                    throw error
                }
                controller.didLaunch()

                let stdoutTask = Task.detached(priority: .utility) {
                    PipeOutputReader.drain(stdout.fileHandleForReading, limit: maximumStandardOutputBytes)
                }
                let stderrTask = Task.detached(priority: .utility) {
                    PipeOutputReader.drain(stderr.fileHandleForReading, limit: maximumStandardErrorBytes)
                }
                process.waitUntilExit()
                let output = await stdoutTask.value
                let errorOutput = await stderrTask.value

                if controller.isCancellationRequested { throw CancellationError() }
                if output.exceededLimit {
                    throw ProcessExecutionError.outputLimitExceeded(stream: "stdout", limit: maximumStandardOutputBytes)
                }
                if errorOutput.exceededLimit {
                    throw ProcessExecutionError.outputLimitExceeded(stream: "stderr", limit: maximumStandardErrorBytes)
                }
                return ProcessExecutionResult(
                    standardOutput: output.data,
                    standardError: errorOutput.data,
                    terminationStatus: process.terminationStatus
                )
            }.value
        } onCancel: {
            controller.cancel()
        }
    }
}
