#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
cat > "$TMP/main.swift" <<'SWIFT'
import Foundation

@main
struct ProcessExecutorTest {
    static func main() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
        let normal = try await ProcessExecutor.run(executable: "/bin/echo", arguments: ["stdout-check"], at: root)
        guard normal.exitCode == 0, String(decoding: normal.standardOutput, as: UTF8.self).contains("stdout-check") else {
            fatalError("stdout/exit code were not captured")
        }
        let failure = try await ProcessExecutor.run(executable: "/usr/bin/false", arguments: [], at: root)
        guard failure.exitCode != 0 else { fatalError("nonzero process exit was lost") }

        let task = Task {
            try await ProcessExecutor.run(executable: "/bin/sleep", arguments: ["10"], at: root)
        }
        try await Task.sleep(nanoseconds: 150_000_000)
        task.cancel()
        do {
            _ = try await task.value
            fatalError("cancelled process returned success")
        } catch is CancellationError {
            print("Process executor tests passed: stdout, nonzero exit code, cancellation.")
        }
    }
}
SWIFT
swiftc -parse-as-library "$ROOT/macOSApp/Sources/CodeAtlas/Infrastructure/ProcessExecutor.swift" "$TMP/main.swift" -o "$TMP/process-executor-test"
"$TMP/process-executor-test"
