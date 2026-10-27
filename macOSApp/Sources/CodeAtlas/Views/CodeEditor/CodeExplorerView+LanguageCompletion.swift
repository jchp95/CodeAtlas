import Foundation
import SwiftUI

extension CodeExplorerView {
    func insertLanguageCompletion(_ item: LanguageCompletionItem) {
        let source = editorText as NSString
        let offset = min(max(0, caretOffset), source.length)
        var replacementStart: Int?
        var replacementEnd: Int?
        if let range = item.textEditRange {
            let start = utf16Offset(in: editorText, line: range.startLine, character: range.startCharacter)
            let end = utf16Offset(in: editorText, line: range.endLine, character: range.endCharacter)
            if start <= end && end <= source.length {
                replacementStart = start
                replacementEnd = end
            }
        }
        if replacementStart == nil || replacementEnd == nil {
            var prefixStart = offset
            while prefixStart > 0 {
                let unit = source.character(at: prefixStart - 1)
                let isIdentifierUnit = (unit >= 48 && unit <= 57) || (unit >= 65 && unit <= 90) || (unit >= 97 && unit <= 122) || unit == 95 || unit == 36
                guard isIdentifierUnit else { break }
                prefixStart -= 1
            }
            replacementStart = prefixStart
            replacementEnd = offset
        }
        guard let start = replacementStart, let end = replacementEnd else { return }
        let inserted = item.insertText
        editorText = source.replacingCharacters(in: NSRange(location: start, length: end - start), with: inserted)
        caretOffset = start + (inserted as NSString).length
        targetLine = lineNumber(in: editorText, offset: caretOffset)
    }

    private func utf16Offset(in text: String, line: Int, character: Int) -> Int {
        let source = text as NSString
        let targetLine = max(0, line)
        var lineStart = 0
        if targetLine > 0 {
            var currentLine = 0
            while currentLine < targetLine && lineStart < source.length {
                let newline = source.range(of: "\n", options: [], range: NSRange(location: lineStart, length: source.length - lineStart))
                guard newline.location != NSNotFound else { return source.length }
                lineStart = newline.location + newline.length
                currentLine += 1
            }
        }
        let remaining = max(0, source.length - lineStart)
        let newline = source.range(of: "\n", options: [], range: NSRange(location: lineStart, length: remaining))
        let lineEnd = newline.location == NSNotFound ? source.length : newline.location
        return min(lineStart + max(0, character), lineEnd)
    }

 }
