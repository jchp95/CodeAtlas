import Foundation

private func check(_ condition: @autoclosure () -> Bool, _ message: String) {
    guard condition() else {
        fputs("Language-services regression failed: \(message)\n", stderr)
        exit(1)
    }
}

private final class DiagnosticBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [LanguageDiagnostic] = []
    func set(_ diagnostics: [LanguageDiagnostic]) { lock.withLock { stored = diagnostics } }
    func get() -> [LanguageDiagnostic] { lock.withLock { stored } }
}

@main
struct LanguageServicesRegressionTests {
    static func main() async throws {
        try testCodecAndModels()
        try await testRunningLanguageServer()
        print("Language-services regression tests passed: framed JSON-RPC, safety limits, language mapping, diagnostics, server handshake, configuration request, completion, definition and document synchronization.")
    }

    private static func testCodecAndModels() throws {
        let first = Data("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}".utf8)
        let second = Data("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}".utf8)
        let firstFrame = LSPFrameCodec.frame(first)
        let secondFrame = LSPFrameCodec.frame(second)
        var codec = LSPFrameCodec(maximumPayloadBytes: 4096)
        let split = firstFrame.count / 2
        let partial = try codec.append(firstFrame.subdata(in: 0..<split))
        check(partial.isEmpty, "partial frame must stay buffered")
        let completed = try codec.append(firstFrame.subdata(in: split..<firstFrame.count))
        check(completed == [first], "fragmented frame must decode")
        let combined = try codec.append(secondFrame + firstFrame)
        check(combined == [second, first], "multiple frames must preserve wire order")

        var invalid = LSPFrameCodec()
        do { _ = try invalid.append(Data("X-Test: 1\r\n\r\n{}".utf8)); check(false, "missing Content-Length must fail") }
        catch LSPFrameCodecError.missingContentLength { }
        var oversized = LSPFrameCodec(maximumPayloadBytes: 4)
        do { _ = try oversized.append(Data("Content-Length: 5\r\n\r\n".utf8)); check(false, "oversized payload must fail") }
        catch LSPFrameCodecError.payloadTooLarge(let count) { check(count == 5, "oversized payload diagnostic is precise") }

        check(LanguageServerLaunchConfiguration.preferred(forFileExtension: "cs")?.languageID == "csharp", "C# maps to csharp")
        check(LanguageServerLaunchConfiguration.preferred(forFileExtension: "tsx")?.languageID(forFileExtension: "tsx") == "typescriptreact", "TSX uses React-specific LSP language ID")
        check(LanguageServerLaunchConfiguration.preferred(forFileExtension: "jsx")?.languageID(forFileExtension: "jsx") == "javascriptreact", "JSX uses React-specific LSP language ID")
        check(LanguageServerLaunchConfiguration.preferred(forFileExtension: "sql") == nil, "unsupported extensions stay unsupported")

        let diagnostic = LanguageDiagnostic(json: [
            "message": "Type mismatch", "severity": 1, "source": "csharp-ls", "code": "CS0029",
            "range": ["start": ["line": 2, "character": 4], "end": ["line": 2, "character": 8]]
        ], uri: "file:///tmp/Test.cs")
        check(diagnostic?.line == 3 && diagnostic?.column == 5, "LSP ranges map to one-based editor coordinates")
        check(diagnostic?.code == "CS0029" && diagnostic?.severityLabel == "Error", "diagnostic code and severity parse")
        let completion = LanguageCompletionItem(json: ["label": "ApplyAsync", "textEdit": [
            "newText": "ApplyAsync", "range": ["start": ["line": 0, "character": 6], "end": ["line": 0, "character": 9]]
        ]])
        check(completion?.insertText == "ApplyAsync", "completion uses textEdit.newText when present")
        check(completion?.textEditRange?.startCharacter == 6 && completion?.textEditRange?.endCharacter == 9, "completion preserves LSP replacement range")
    }

    private static func testRunningLanguageServer() async throws {
        let arguments = CommandLine.arguments
        guard arguments.count == 3 else { throw NSError(domain: "LanguageServicesTests", code: 2, userInfo: [NSLocalizedDescriptionKey: "Expected fake server path and target source path."]) }
        let executable = URL(fileURLWithPath: arguments[1]).standardizedFileURL
        let targetURL = URL(fileURLWithPath: arguments[2]).standardizedFileURL
        let configuration = LanguageServerLaunchConfiguration(identifier: "fake-lsp", executable: executable.path,
            arguments: [targetURL.absoluteString], languageID: "csharp", extensions: ["cs"])
        let root = targetURL.deletingLastPathComponent()
        let client = LanguageServerClient()
        let diagnostics = DiagnosticBox()
        client.onDiagnostics = { _, values in diagnostics.set(values) }

        try await client.start(configuration: configuration, workspaceRoot: root)
        try await client.openDocument(url: root.appendingPathComponent("Open.cs"), languageID: "csharp", text: "class Open { void Run() {} }")
        for _ in 0..<40 {
            if !diagnostics.get().isEmpty { break }
            try await Task.sleep(nanoseconds: 25_000_000)
        }
        let received = diagnostics.get()
        check(received.count == 1 && received[0].message == "fake diagnostic", "publishDiagnostics notification received")
        let completions = try await client.completion(url: root.appendingPathComponent("Open.cs"), line: 0, character: 10)
        check(completions.contains(where: { $0.label == "FakeCompletion" }), "completion request correlated after server configuration request")
        let definition = try await client.definition(url: root.appendingPathComponent("Open.cs"), line: 0, character: 10)
        check(definition?.url.standardizedFileURL.path == targetURL.path && definition?.line == 4, "definition location parsed")
        try client.changeDocument(text: "class Open { void Run() { } }")
        await client.shutdown()
    }
}
