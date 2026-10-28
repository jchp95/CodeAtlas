import Foundation

@main
struct EngineInvocationResolverRegressionTests {
    static func main() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("codeatlas-engine-resolver-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporaryRoot, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let project = temporaryRoot.appendingPathComponent("CodeAtlas.Engine.csproj")
        try Data("test".utf8).write(to: project)

        let command = ["scan", "/workspace/root"]
        let fallback = EngineInvocationResolver.arguments(engineDirectory: temporaryRoot, commandArguments: command)
        try expect(fallback.first == "run" && fallback.suffix(2).elementsEqual(command), "Missing assembly should use dotnet run fallback")

        let release = temporaryRoot.appendingPathComponent("bin/Release/net10.0/CodeAtlas.Engine.dll")
        try FileManager.default.createDirectory(at: release.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data().write(to: release)
        let releaseArgs = EngineInvocationResolver.arguments(engineDirectory: temporaryRoot, commandArguments: command)
        try expect(releaseArgs.first == release.path && releaseArgs.suffix(2).elementsEqual(command), "Release assembly should be selected if Debug is absent")

        let debug = temporaryRoot.appendingPathComponent("bin/Debug/net10.0/CodeAtlas.Engine.dll")
        try FileManager.default.createDirectory(at: debug.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data().write(to: debug)
        let debugArgs = EngineInvocationResolver.arguments(engineDirectory: temporaryRoot, commandArguments: command)
        try expect(debugArgs.first == debug.path && debugArgs.suffix(2).elementsEqual(command), "Debug assembly should be preferred after the startup build")
        print("Engine invocation resolver tests passed: cached Debug/Release assembly preference and clean-checkout fallback.")
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
