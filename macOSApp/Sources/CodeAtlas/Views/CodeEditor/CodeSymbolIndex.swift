import Foundation

struct CodeOutlineSymbol: Identifiable, Hashable, Sendable {
    let name: String
    let kind: String
    let line: Int
    var id: String { "\(line)|\(kind)|\(name)" }
}

/// Fast, best-effort outline index for the native editor. This is deliberately
/// labeled heuristic: language-server symbols will replace it when LSP is available.
enum CodeSymbolIndex {
    private struct Rule {
        let regex: NSRegularExpression
        let kind: String
        let nameGroup: Int
    }

    static func scan(_ source: String, fileExtension: String) -> [CodeOutlineSymbol] {
        let ext = fileExtension.lowercased()
        let definitions: [(String, String, Int)]
        switch ext {
        case "cs", "java", "kt", "go", "rs":
            definitions = [
                (#"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|async|final|export|open)\s+)*(class|record|interface|enum|struct|trait)\s+([A-Za-z_][A-Za-z0-9_]*)"#, "type", 2),
                (#"^\s*(?:(?:public|private|protected|internal|static|async|override|virtual|final|export)\s+)*[A-Za-z_][A-Za-z0-9_<>,.?\[\]: ]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\("#, "method", 1)
            ]
        case "ts", "tsx", "js", "jsx", "mjs", "cjs":
            definitions = [
                (#"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?(class|interface|type|enum)\s+([A-Za-z_$][A-Za-z0-9_$]*)"#, "type", 2),
                (#"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)"#, "function", 1),
                (#"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][A-Za-z0-9_$]*)\s*=>"#, "function", 1)
            ]
        case "swift":
            definitions = [
                (#"^\s*(?:(?:public|private|fileprivate|internal|open|final|actor)\s+)*(struct|class|enum|protocol|actor)\s+([A-Za-z_][A-Za-z0-9_]*)"#, "type", 2),
                (#"^\s*(?:(?:public|private|fileprivate|internal|static|class|override|mutating|async|throws)\s+)*func\s+([A-Za-z_][A-Za-z0-9_]*)"#, "function", 1)
            ]
        case "py":
            definitions = [
                (#"^\s*class\s+([A-Za-z_][A-Za-z0-9_]*)"#, "type", 1),
                (#"^\s*(?:async\s+)?def\s+([A-Za-z_][A-Za-z0-9_]*)"#, "function", 1)
            ]
        case "md", "markdown", "mdx":
            definitions = [(#"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$"#, "heading", 1)]
        case "json", "jsonc":
            definitions = [(#"^\s*"([^"]+)"\s*:"#, "key", 1)]
        default:
            definitions = []
        }

        let rules: [Rule] = definitions.compactMap { pattern, kind, group in
            guard let regex = try? NSRegularExpression(pattern: pattern) else { return nil }
            return Rule(regex: regex, kind: kind, nameGroup: group)
        }
        guard !rules.isEmpty else { return [] }

        var results: [CodeOutlineSymbol] = []
        let lines = source.components(separatedBy: "\n")
        for (index, line) in lines.enumerated() {
            let nsLine = line as NSString
            let range = NSRange(location: 0, length: nsLine.length)
            for rule in rules {
                guard let match = rule.regex.firstMatch(in: line, range: range),
                      rule.nameGroup < match.numberOfRanges,
                      match.range(at: rule.nameGroup).location != NSNotFound else { continue }
                let name = nsLine.substring(with: match.range(at: rule.nameGroup)).trimmingCharacters(in: .whitespacesAndNewlines)
                guard !name.isEmpty else { continue }
                results.append(CodeOutlineSymbol(name: name, kind: rule.kind, line: index + 1))
                if results.count >= 2_000 { return results }
                break
            }
        }
        return results
    }
}
