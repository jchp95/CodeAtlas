import Foundation

struct ProcessExecutionResult: Sendable {
    let standardOutput: Data
    let standardError: Data
    let exitCode: Int32
}

/// Runs child processes away from the UI executor and terminates them when the
/// calling Swift task is cancelled. Arguments are passed directly; no shell is used.
private final class ProcessCancellationHandle: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var cancellationRequested = false

    func attach(_ process: Process) {
        lock.lock()
        self.process = process
        let shouldCancel = cancellationRequested
        lock.unlock()
        if shouldCancel, process.isRunning { process.terminate() }
    }

    func cancel() {
        lock.lock()
        cancellationRequested = true
        let process = self.process
        lock.unlock()
        if let process, process.isRunning { process.terminate() }
    }

    var isCancellationRequested: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancellationRequested
    }
}

enum ProcessExecutor {
    static func run(
        executable: String,
        arguments: [String],
        at directory: URL,
        environment: [String: String]? = nil,
        priority: TaskPriority = .userInitiated
    ) async throws -> ProcessExecutionResult {
        let cancellation = ProcessCancellationHandle()
        return try await withTaskCancellationHandler {
            try await Task.detached(priority: priority) {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: executable)
                process.arguments = arguments
                process.currentDirectoryURL = directory
                if let environment { process.environment = environment }

                let stdout = Pipe()
                let stderr = Pipe()
                process.standardOutput = stdout
                process.standardError = stderr
                cancellation.attach(process)
                do {
                    try process.run()
                } catch {
                    throw NSError(domain: "CodeAtlas.Process", code: 1, userInfo: [
                        NSLocalizedDescriptionKey: "No se pudo iniciar \(executable): \(error.localizedDescription)"
                    ])
                }
                if cancellation.isCancellationRequested, process.isRunning { process.terminate() }

                let outputTask = Task.detached(priority: .utility) {
                    stdout.fileHandleForReading.readDataToEndOfFile()
                }
                let errorTask = Task.detached(priority: .utility) {
                    stderr.fileHandleForReading.readDataToEndOfFile()
                }
                process.waitUntilExit()
                let output = await outputTask.value
                let error = await errorTask.value
                if cancellation.isCancellationRequested { throw CancellationError() }
                return ProcessExecutionResult(
                    standardOutput: output,
                    standardError: error,
                    exitCode: process.terminationStatus
                )
            }.value
        } onCancel: {
            cancellation.cancel()
        }
    }
}
