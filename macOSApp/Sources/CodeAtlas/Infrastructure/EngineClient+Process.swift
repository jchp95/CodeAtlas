import Foundation

extension EngineClient {
    func runEngine(arguments: [String]) async throws -> Data {
        let engine = try Self.resolveEngineDirectory()
        let dotnetArguments = EngineInvocationResolver.arguments(engineDirectory: engine, commandArguments: arguments)
        let result: ProcessExecutionResult
        do {
            result = try await ProcessExecutionService.run(
                executableURL: URL(fileURLWithPath: "/usr/bin/env"),
                arguments: ["dotnet"] + dotnetArguments,
                currentDirectoryURL: engine
            )
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw EngineError.processFailed(error.localizedDescription)
        }

        guard result.terminationStatus == 0 else {
            let message = String(data: result.standardError, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
            throw EngineError.processFailed(message?.isEmpty == false ? message! : "dotnet terminó con código \(result.terminationStatus)")
        }
        return result.standardOutput
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
        let result: ProcessExecutionResult
        do {
            result = try await ProcessExecutionService.run(
                executableURL: URL(fileURLWithPath: "/usr/bin/git"),
                arguments: arguments,
                currentDirectoryURL: directory,
                maximumStandardOutputBytes: 64 * 1024 * 1024,
                maximumStandardErrorBytes: 4 * 1024 * 1024
            )
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw EngineError.gitFailed(error.localizedDescription)
        }
        guard result.terminationStatus == 0 else {
            let message = String(data: result.standardError, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
            throw EngineError.gitFailed(message?.isEmpty == false ? message! : "git terminó con código \(result.terminationStatus)")
        }
        return String(data: result.standardOutput, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

}
