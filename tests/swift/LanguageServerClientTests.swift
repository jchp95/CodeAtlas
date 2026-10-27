import Foundation

func assertTest(_ condition: @autoclosure () -> Bool, _ message: String) {
    guard condition() else {
        fputs("Language-server client test failed: \(message)\n", stderr)
        exit(1)
    }
}

@main
struct LanguageServerClientTests {
    static func main() async throws {
        let arguments = CommandLine.arguments
        guard arguments.count == 3 else {
            fputs("Expected fake server path and target source path.\n", stderr)
            exit(2)
        }
        let executable = URL(fileURLWithPath: arguments[1]).standardizedFileURL
        let targetURL = URL(fileURLWithPath: arguments[2]).standardizedFileURL
        let configuration = LanguageServerLaunchConfiguration(
            identifier: "fake-lsp", executable: executable.path, arguments: [targetURL.absoluteString],
            languageID: "csharp", extensions: ["cs"]
        )
        let root = targetURL.deletingLastPathComponent()
        let client = LanguageServerClient()
        let diagnosticsLock = NSLock()
        var receivedDiagnostics: [LanguageDiagnostic] = []
        client.onDiagnostics = { _, values in
            diagnosticsLock.lock()
            receivedDiagnostics = values
            diagnosticsLock.unlock()
        }

        try await client.start(configuration: configuration, workspaceRoot: root)
        try await client.openDocument(url: root.appendingPathComponent("Open.cs"), languageID: "csharp", text: "class Open { void Run() {} }")

        for _ in 0..<40 {
            diagnosticsLock.lock()
            let hasDiagnostics = !receivedDiagnostics.isEmpty
            diagnosticsLock.unlock()
            if hasDiagnostics { break }
            try await Task.sleep(nanoseconds: 25_000_000)
        }
        diagnosticsLock.lock()
        let diagnostics = receivedDiagnostics
        diagnosticsLock.unlock()
        assertTest(diagnostics.count == 1 && diagnostics[0].message == "fake diagnostic", "client receives publishDiagnostics notification")

        let completion = try await client.completion(url: root.appendingPathComponent("Open.cs"), line: 0, character: 10)
        assertTest(completion.contains(where: { $0.label == "FakeCompletion" }), "completion request is correlated to the correct JSON-RPC response")

        let definition = try await client.definition(url: root.appendingPathComponent("Open.cs"), line: 0, character: 10)
        assertTest(definition?.url.standardizedFileURL.path == targetURL.path && definition?.line == 4, "definition location URI and one-based line are parsed")

        try client.changeDocument(text: "class Open { void Run() { } }")
        await client.shutdown()
        print("Language-server client integration tests passed: initialize, didOpen, diagnostics, completion, definition, didChange and shutdown.")
    }
}
