import Foundation

/// An explicit, shell-free command for an optional language server.
/// CodeAtlas never installs a server automatically and never runs a command string through a shell.
struct LanguageServerLaunchConfiguration: Equatable, Sendable {
    let identifier: String
    let executable: String
    let arguments: [String]
    let languageID: String
    let extensions: Set<String>

    static let supported: [LanguageServerLaunchConfiguration] = [
        .init(identifier: "csharp-ls", executable: "csharp-ls", arguments: [], languageID: "csharp", extensions: ["cs"]),
        .init(identifier: "typescript-language-server", executable: "typescript-language-server", arguments: ["--stdio"], languageID: "typescript", extensions: ["ts", "tsx"]),
        .init(identifier: "typescript-language-server-js", executable: "typescript-language-server", arguments: ["--stdio"], languageID: "javascript", extensions: ["js", "jsx", "mjs", "cjs"]),
        .init(identifier: "sourcekit-lsp", executable: "sourcekit-lsp", arguments: [], languageID: "swift", extensions: ["swift"]),
        .init(identifier: "python-lsp-server", executable: "pylsp", arguments: [], languageID: "python", extensions: ["py"]),
        .init(identifier: "vscode-json-language-server", executable: "vscode-json-language-server", arguments: ["--stdio"], languageID: "json", extensions: ["json", "jsonc"])
    ]

    static func preferred(forFileExtension value: String) -> LanguageServerLaunchConfiguration? {
        let ext = value.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "."))
        return supported.first { $0.extensions.contains(ext) }
    }

    func languageID(forFileExtension value: String) -> String {
        switch value.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: ".")) {
        case "tsx": return "typescriptreact"
        case "jsx": return "javascriptreact"
        default: return languageID
        }
    }

    /// Resolves an executable from an explicit path or PATH. Does not invoke a shell.
    func resolveExecutable(searchPath: String = ProcessInfo.processInfo.environment["PATH"] ?? "") -> URL? {
        if executable.contains("/") {
            let candidate = URL(fileURLWithPath: executable).standardizedFileURL
            return Self.isExecutable(candidate) ? candidate : nil
        }
        for directory in searchPath.split(separator: ":", omittingEmptySubsequences: true) {
            let candidate = URL(fileURLWithPath: String(directory), isDirectory: true)
                .appendingPathComponent(executable).standardizedFileURL
            if Self.isExecutable(candidate) { return candidate }
        }
        return nil
    }

    private static func isExecutable(_ url: URL) -> Bool {
        FileManager.default.isExecutableFile(atPath: url.path)
    }
}
