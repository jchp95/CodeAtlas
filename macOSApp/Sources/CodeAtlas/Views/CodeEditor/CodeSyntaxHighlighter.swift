import AppKit
import Foundation

enum CodeSyntaxHighlighter {
    private struct Rule {
        let pattern: String
        let color: NSColor
        let weight: NSFont.Weight
    }

    static func apply(to textView: NSTextView, language: String) {
        guard let storage = textView.textStorage else { return }
        let source = textView.string as NSString
        let range = NSRange(location: 0, length: source.length)
        let selection = textView.selectedRange()
        let baseFont = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        let baseAttributes: [NSAttributedString.Key: Any] = [.font: baseFont, .foregroundColor: NSColor.textColor]

        storage.beginEditing()
        storage.setAttributes(baseAttributes, range: range)
        if source.length <= 300_000 {
            for rule in rules(for: language) {
                guard let regex = try? NSRegularExpression(pattern: rule.pattern, options: [.anchorsMatchLines]) else { continue }
                let font = NSFont.monospacedSystemFont(ofSize: 12, weight: rule.weight)
                regex.enumerateMatches(in: source as String, range: range) { match, _, _ in
                    guard let match, match.range.length > 0 else { return }
                    storage.addAttributes([.foregroundColor: rule.color, .font: font], range: match.range)
                }
            }
        }
        storage.endEditing()
        textView.typingAttributes = baseAttributes
        textView.setSelectedRange(NSRange(location: min(selection.location, source.length), length: 0))
    }

    private static func rules(for language: String) -> [Rule] {
        let ext = language.lowercased()
        let keywords: String
        switch ext {
        case "shell", "bash", "zsh":
            keywords = #"\b(if|then|else|elif|fi|for|while|do|done|case|esac|function|in|local|export|return|until|break|continue)\b"#
        case "python", "py":
            keywords = #"\b(and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|nonlocal|not|or|pass|raise|return|True|try|while|with|yield)\b"#
        case "json", "jsonc":
            keywords = #"\b(true|false|null)\b"#
        default:
            keywords = #"\b(abstract|as|async|await|base|break|case|catch|class|const|continue|default|defer|delegate|do|else|enum|event|export|extends|false|finally|for|foreach|func|function|get|if|implements|import|in|interface|internal|let|match|namespace|new|nil|null|open|override|package|private|protected|public|readonly|record|ref|return|self|static|struct|switch|this|throw|throws|trait|try|type|typeof|var|virtual|void|where|while|yield|true|undefined)\b"#
        }
        return [
            Rule(pattern: keywords, color: .systemPurple, weight: .semibold),
            Rule(pattern: #"\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b"#, color: .systemOrange, weight: .regular),
            Rule(pattern: #"\b[A-Z][A-Za-z0-9_]*\b"#, color: .systemTeal, weight: .medium),
            Rule(pattern: #"\b[A-Za-z_$][A-Za-z0-9_$]*(?=\s*\()"#, color: .systemBlue, weight: .medium),
            Rule(pattern: #"@[A-Za-z_$][A-Za-z0-9_$]*"#, color: .systemPink, weight: .regular),
            Rule(pattern: #""(?:\\.|[^"\\])*""#, color: .systemRed, weight: .regular),
            Rule(pattern: #"'(?:\\.|[^'\\])*'"#, color: .systemRed, weight: .regular),
            Rule(pattern: #"//[^\n]*|/\*[\s\S]*?\*/"#, color: .secondaryLabelColor, weight: .regular),
            Rule(pattern: #"#[^\n]*"#, color: .secondaryLabelColor, weight: .regular)
        ]
    }
}
