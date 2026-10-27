import Foundation

enum CommandRunner {
    static func run(executable: String, arguments: [String], at directory: URL) async throws -> String {
        try await Task.detached(priority: .userInitiated) {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            process.arguments = [executable] + arguments
            process.currentDirectoryURL = directory
            var environment = ProcessInfo.processInfo.environment
            environment["PATH"] = (["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin", environment["PATH"] ?? ""]).joined(separator: ":")
            process.environment = environment
            let stdout = Pipe()
            let stderr = Pipe()
            process.standardOutput = stdout
            process.standardError = stderr
            do { try process.run() }
            catch { throw EngineError.gitFailed("No se pudo iniciar \(executable): \(error.localizedDescription)") }
            let outputTask = Task.detached { stdout.fileHandleForReading.readDataToEndOfFile() }
            let errorTask = Task.detached { stderr.fileHandleForReading.readDataToEndOfFile() }
            process.waitUntilExit()
            let output = await outputTask.value
            let error = await errorTask.value
            guard process.terminationStatus == 0 else {
                let detail = String(data: error, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
                throw EngineError.gitFailed(detail?.isEmpty == false ? detail! : "\(executable) terminó con código \(process.terminationStatus).")
            }
            return String(data: output, encoding: .utf8) ?? ""
        }.value
    }
}
