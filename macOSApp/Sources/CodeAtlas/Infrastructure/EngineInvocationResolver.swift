import Foundation

/// Prefer the assembly already produced by the startup verification gate. Falling back to
/// `dotnet run` keeps a source checkout usable before its first build.
enum EngineInvocationResolver {
    static func arguments(engineDirectory: URL, commandArguments: [String]) -> [String] {
        let debugAssembly = engineDirectory.appendingPathComponent("bin/Debug/net10.0/CodeAtlas.Engine.dll")
        let releaseAssembly = engineDirectory.appendingPathComponent("bin/Release/net10.0/CodeAtlas.Engine.dll")
        if FileManager.default.fileExists(atPath: debugAssembly.path) {
            return [debugAssembly.path] + commandArguments
        }
        if FileManager.default.fileExists(atPath: releaseAssembly.path) {
            return [releaseAssembly.path] + commandArguments
        }
        return [
            "run", "--verbosity", "quiet",
            "--project", engineDirectory.appendingPathComponent("CodeAtlas.Engine.csproj").path,
            "--"
        ] + commandArguments
    }
}
