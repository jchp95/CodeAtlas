import Foundation

@main
struct ProcessExecutionRegressionTests {
    static func main() async throws {
        try await successfulCommandCapturesBothStreams()
        try await nonZeroExitIsPreserved()
        try await outputIsBounded()
        try await cancellationTerminatesProcess()
        print("Process execution regression tests passed: stdout/stderr draining, exit status, bounded output, and cancellation.")
    }

    static func successfulCommandCapturesBothStreams() async throws {
        let result = try await ProcessExecutionService.run(
            executableURL: URL(fileURLWithPath: "/usr/bin/env"),
            arguments: ["node", "-e", "process.stdout.write('out'); process.stderr.write('err')"]
        )
        try expect(result.terminationStatus == 0, "Successful process should return zero")
        try expect(String(data: result.standardOutput, encoding: .utf8) == "out", "stdout should be captured")
        try expect(String(data: result.standardError, encoding: .utf8) == "err", "stderr should be captured")
    }

    static func nonZeroExitIsPreserved() async throws {
        let result = try await ProcessExecutionService.run(
            executableURL: URL(fileURLWithPath: "/usr/bin/env"),
            arguments: ["node", "-e", "process.exit(7)"]
        )
        try expect(result.terminationStatus == 7, "Non-zero process status should be preserved")
    }

    static func outputIsBounded() async throws {
        do {
            _ = try await ProcessExecutionService.run(
                executableURL: URL(fileURLWithPath: "/usr/bin/env"),
                arguments: ["node", "-e", "process.stdout.write('x'.repeat(100000))"],
                maximumStandardOutputBytes: 256
            )
            throw TestFailure(message: "Expected stdout output limit to be enforced")
        } catch let error as ProcessExecutionError {
            guard case .outputLimitExceeded(let stream, let limit) = error else {
                throw TestFailure(message: "Unexpected ProcessExecutionError: \(error)")
            }
            try expect(stream == "stdout" && limit == 256, "Output limit error should identify the stream and limit")
        }
    }

    static func cancellationTerminatesProcess() async throws {
        let task = Task {
            try await ProcessExecutionService.run(
                executableURL: URL(fileURLWithPath: "/usr/bin/env"),
                arguments: ["node", "-e", "setTimeout(() => process.stdout.write('too late'), 15000)"]
            )
        }
        try await Task.sleep(nanoseconds: 150_000_000)
        let start = Date()
        task.cancel()
        do {
            _ = try await task.value
            throw TestFailure(message: "Cancelled process should not return successfully")
        } catch is CancellationError {
            try expect(Date().timeIntervalSince(start) < 4, "Cancellation should terminate a long-running process promptly")
        }
    }

    static func expect(_ condition: @autoclosure () -> Bool, _ message: String) throws {
        if !condition() { throw TestFailure(message: message) }
    }
}

struct TestFailure: Error, CustomStringConvertible {
    let message: String
    init(message: String) { self.message = message }
    var description: String { message }
}
