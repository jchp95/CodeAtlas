import Foundation

struct LanguageDiagnostic: Identifiable, Hashable, Sendable {
    let uri: String
    let line: Int
    let column: Int
    let endLine: Int
    let endColumn: Int
    let severity: Int
    let message: String
    let source: String?
    let code: String?

    var id: String { "\(uri)|\(line)|\(column)|\(severity)|\(message)" }
    var severityLabel: String {
        switch severity {
        case 1: return "Error"
        case 2: return "Warning"
        case 3: return "Info"
        case 4: return "Hint"
        default: return "Diagnostic"
        }
    }

    init?(json: [String: Any], uri: String) {
        guard let message = json["message"] as? String else { return nil }
        let range = json["range"] as? [String: Any] ?? [:]
        let start = range["start"] as? [String: Any] ?? [:]
        let end = range["end"] as? [String: Any] ?? [:]
        self.uri = uri
        self.line = max(1, (start["line"] as? Int ?? 0) + 1)
        self.column = max(1, (start["character"] as? Int ?? 0) + 1)
        self.endLine = max(1, (end["line"] as? Int ?? (self.line - 1)) + 1)
        self.endColumn = max(1, (end["character"] as? Int ?? (self.column - 1)) + 1)
        self.severity = json["severity"] as? Int ?? 3
        self.message = message
        self.source = json["source"] as? String
        if let code = json["code"] as? String { self.code = code }
        else if let code = json["code"] as? Int { self.code = String(code) }
        else { self.code = nil }
    }
}

struct LanguageTextEditRange: Hashable, Sendable {
    let startLine: Int
    let startCharacter: Int
    let endLine: Int
    let endCharacter: Int

    init?(json: [String: Any]?) {
        guard let json else { return nil }
        let start: [String: Any]
        let end: [String: Any]
        if let range = json["range"] as? [String: Any] {
            start = range["start"] as? [String: Any] ?? [:]
            end = range["end"] as? [String: Any] ?? [:]
        } else if let range = json["replace"] as? [String: Any] {
            // InsertReplaceEdit: prefer the larger replacement range to avoid duplicated prefixes.
            start = range["start"] as? [String: Any] ?? [:]
            end = range["end"] as? [String: Any] ?? [:]
        } else { return nil }
        guard let startLine = start["line"] as? Int,
              let startCharacter = start["character"] as? Int,
              let endLine = end["line"] as? Int,
              let endCharacter = end["character"] as? Int else { return nil }
        self.startLine = startLine
        self.startCharacter = startCharacter
        self.endLine = endLine
        self.endCharacter = endCharacter
    }
}

struct LanguageCompletionItem: Identifiable, Hashable, Sendable {
    let label: String
    let detail: String?
    let insertText: String
    let kind: Int?
    let sortText: String?
    let textEditRange: LanguageTextEditRange?

    var id: String { "\(label)|\(detail ?? "")|\(insertText)" }

    init?(json: [String: Any]) {
        guard let label = json["label"] as? String, !label.isEmpty else { return nil }
        self.label = label
        self.detail = json["detail"] as? String
        let textEdit = json["textEdit"] as? [String: Any]
        self.insertText = (json["insertText"] as? String) ?? (textEdit?["newText"] as? String) ?? label
        self.kind = json["kind"] as? Int
        self.sortText = json["sortText"] as? String
        self.textEditRange = LanguageTextEditRange(json: textEdit)
    }
}

struct LanguageDefinitionLocation: Hashable, Sendable {
    let url: URL
    let line: Int
    let column: Int
}

struct LanguageServerStatus: Hashable, Sendable {
    enum State: String, Sendable {
        case unavailable
        case starting
        case ready
        case failed
    }

    var state: State
    var serverName: String
    var message: String

    static let unavailable = LanguageServerStatus(state: .unavailable, serverName: "LSP", message: "Selecciona un archivo compatible")
}
