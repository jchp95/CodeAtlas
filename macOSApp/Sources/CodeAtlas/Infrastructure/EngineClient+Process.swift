import Foundation

extension EngineClient {
func runEngine(arguments: [String]) async throws -> Data {
        try await Task.detached(priority: .userInitiated) {
            let engine = try Self.resolveEngineDirectory()
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            process.arguments = [
                "dotnet", "run", "--verbosity", "quiet",
                "--project", engine.appendingPathComponent("CodeAtlas.Engine.csproj").path,
                "--"
            ] + arguments
            process.currentDirectoryURL = engine

            let stdout = Pipe()
            let stderr = Pipe()
            process.standardOutput = stdout
            process.standardError = stderr
            try process.run()
            let outputTask = Task.detached { stdout.fileHandleForReading.readDataToEndOfFile() }
            let errorTask = Task.detached { stderr.fileHandleForReading.readDataToEndOfFile() }
            process.waitUntilExit()
            let outputData = await outputTask.value
            let errorData = await errorTask.value
            guard process.terminationStatus == 0 else {
                throw EngineError.processFailed(String(data: errorData, encoding: .utf8) ?? "exit \(process.terminationStatus)")
            }
            return outputData
        }.value
    }

    static func resolveEngineDirectory() throws -> URL {
        let cwd = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let candidates = [
            cwd.appendingPathComponent("../engine/CodeAtlas.Engine").standardizedFileURL,
            cwd.appendingPathComponent("engine/CodeAtlas.Engine").standardizedFileURL,
            Bundle.main.bundleURL.appendingPathComponent("../Resources/CodeAtlas.Engine").standardizedFileURL
        ]
        guard let engine = candidates.first(where: {
            FileManager.default.fileExists(atPath: $0.appendingPathComponent("CodeAtlas.Engine.csproj").path)
        }) else {
            throw EngineError.engineNotFound(candidates.map(\.path).joined(separator: " | "))
        }
        return engine
    }

    static func runGit(_ arguments: [String], at directory: URL) async throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = arguments
        process.currentDirectoryURL = directory
        let stdout = Pipe()
        let stderr = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        do {
            try process.run()
        } catch {
            throw EngineError.gitFailed(error.localizedDescription)
        }
        let outputTask = Task.detached { stdout.fileHandleForReading.readDataToEndOfFile() }
        let errorTask = Task.detached { stderr.fileHandleForReading.readDataToEndOfFile() }
        process.waitUntilExit()
        let outputData = await outputTask.value
        let errorData = await errorTask.value
        guard process.terminationStatus == 0 else {
            throw EngineError.gitFailed(String(data: errorData, encoding: .utf8) ?? "exit \(process.terminationStatus)")
        }
        return String(data: outputData, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }
}
