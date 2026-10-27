import Foundation

func check(_ condition: @autoclosure () -> Bool, _ message: String) {
    guard condition() else {
        fputs("Language-service foundation test failed: \(message)\n", stderr)
        exit(1)
    }
}

@main
struct LanguageServiceFoundationTests {
    static func main() throws {
        let first = Data("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}".utf8)
        let second = Data("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}".utf8)
        let firstFrame = LSPFrameCodec.frame(first)
        let secondFrame = LSPFrameCodec.frame(second)
        var codec = LSPFrameCodec(maximumPayloadBytes: 4096)

        let split = firstFrame.count / 2
        let initialFrames = try codec.append(firstFrame.subdata(in: 0..<split))
        check(initialFrames.isEmpty, "partial frame must remain buffered")
        let completedFrames = try codec.append(firstFrame.subdata(in: split..<firstFrame.count))
        check(completedFrames.count == 1 && completedFrames[0] == first, "fragmented frame must be decoded")
        let combinedFrames = try codec.append(secondFrame + firstFrame)
        check(combinedFrames == [second, first], "multiple frames in one read must preserve order")

        var invalid = LSPFrameCodec()
        do {
            _ = try invalid.append(Data("X-Test: 1\r\n\r\n{}".utf8))
            check(false, "missing Content-Length must fail")
        } catch LSPFrameCodecError.missingContentLength { }

        var oversized = LSPFrameCodec(maximumPayloadBytes: 4)
        do {
            _ = try oversized.append(Data("Content-Length: 5\r\n\r\n".utf8))
            check(false, "oversized payload must fail")
        } catch LSPFrameCodecError.payloadTooLarge(let count) {
            check(count == 5, "reported oversized payload length must match")
        }

        let configuration = LanguageServerLaunchConfiguration.preferred(forFileExtension: "cs")
        check(configuration?.languageID == "csharp", "C# extension maps to csharp LSP language ID")
        check(LanguageServerLaunchConfiguration.preferred(forFileExtension: "tsx")?.languageID == "typescript", "TSX maps to TypeScript server")
        check(LanguageServerLaunchConfiguration.preferred(forFileExtension: "sql") == nil, "unsupported extension must not claim LSP support")

        let diagnostic = LanguageDiagnostic(json: [
            "message": "Type mismatch", "severity": 1, "source": "csharp-ls", "code": "CS0029",
            "range": ["start": ["line": 2, "character": 4], "end": ["line": 2, "character": 8]]
        ], uri: "file:///tmp/Test.cs")
        check(diagnostic?.line == 3 && diagnostic?.column == 5, "LSP zero-based diagnostic range maps to one-based editor locations")
        check(diagnostic?.code == "CS0029" && diagnostic?.severityLabel == "Error", "diagnostic code and severity parse")

        let item = LanguageCompletionItem(json: ["label": "ApplyAsync", "detail": "Task", "insertText": "ApplyAsync"])
        check(item?.label == "ApplyAsync" && item?.insertText == "ApplyAsync", "completion item parses label and insert text")
        print("Language-service foundation tests passed: LSP framing, partial reads, message ordering, payload limits, language mapping and diagnostic/completion models.")
    }
}
