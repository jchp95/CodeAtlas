import AppKit
import SwiftUI

struct NativeCodeEditor: NSViewRepresentable {
    @Binding var text: String
    @Binding var caretOffset: Int
    var targetLine: Int
    var language: String

    func makeCoordinator() -> Coordinator { Coordinator(text: $text, caretOffset: $caretOffset, language: language) }

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.borderType = .noBorder

        let textView = NSTextView()
        textView.isRichText = false
        textView.allowsUndo = true
        textView.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        textView.textColor = .textColor
        textView.backgroundColor = .textBackgroundColor
        textView.insertionPointColor = .textColor
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isAutomaticSpellingCorrectionEnabled = false
        textView.isContinuousSpellCheckingEnabled = false
        textView.isGrammarCheckingEnabled = false
        textView.usesFindBar = true
        textView.usesFontPanel = false
        textView.textContainerInset = NSSize(width: 8, height: 10)
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = true
        textView.autoresizingMask = [.width]
        textView.textContainer?.widthTracksTextView = false
        textView.minSize = NSSize(width: 0, height: 0)
        textView.maxSize = NSSize(width: .greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        textView.delegate = context.coordinator
        textView.string = text
        CodeSyntaxHighlighter.apply(to: textView, language: language)
        textView.frame = NSRect(origin: .zero, size: scrollView.contentSize)

        let ruler = CodeLineNumberRulerView(textView: textView, scrollView: scrollView)
        scrollView.verticalRulerView = ruler
        scrollView.hasVerticalRuler = true
        scrollView.rulersVisible = true
        scrollView.documentView = textView
        context.coordinator.textView = textView
        context.coordinator.ruler = ruler
        context.coordinator.lastTargetLine = targetLine
        context.coordinator.lastCaretOffset = caretOffset
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        guard let textView = context.coordinator.textView else { return }
        if textView.string != text {
            let selection = textView.selectedRange()
            textView.string = text
            textView.setSelectedRange(NSRange(location: min(selection.location, (text as NSString).length), length: 0))
            context.coordinator.applySyntaxHighlighting()
            context.coordinator.ruler?.needsDisplay = true
        }
        if context.coordinator.currentLanguage != language {
            context.coordinator.currentLanguage = language
            context.coordinator.applySyntaxHighlighting()
        }
        if context.coordinator.lastTargetLine != targetLine {
            context.coordinator.lastTargetLine = targetLine
            let lines = (textView.string as NSString).components(separatedBy: "\n")
            let boundedLine = min(max(1, targetLine), max(1, lines.count))
            var offset = 0
            if boundedLine > 1 {
                for index in 0..<(boundedLine - 1) { offset += (lines[index] as NSString).length + 1 }
            }
            let safeOffset = min(offset, (textView.string as NSString).length)
            textView.setSelectedRange(NSRange(location: safeOffset, length: 0))
            textView.scrollRangeToVisible(NSRange(location: safeOffset, length: 0))
            context.coordinator.lastCaretOffset = safeOffset
        } else if context.coordinator.lastCaretOffset != caretOffset {
            let safeOffset = min(max(0, caretOffset), (textView.string as NSString).length)
            textView.setSelectedRange(NSRange(location: safeOffset, length: 0))
            textView.scrollRangeToVisible(NSRange(location: safeOffset, length: 0))
            context.coordinator.lastCaretOffset = safeOffset
        }
        context.coordinator.ruler?.needsDisplay = true
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        @Binding private var text: String
        @Binding private var caretOffset: Int
        weak var textView: NSTextView?
        weak var ruler: CodeLineNumberRulerView?
        var lastTargetLine: Int
        var lastCaretOffset: Int
        var currentLanguage: String
        private var isApplyingHighlight = false

        init(text: Binding<String>, caretOffset: Binding<Int>, language: String) {
            _text = text
            _caretOffset = caretOffset
            currentLanguage = language
            lastTargetLine = 1
            lastCaretOffset = caretOffset.wrappedValue
        }

        func applySyntaxHighlighting() {
            guard !isApplyingHighlight, let textView else { return }
            isApplyingHighlight = true
            CodeSyntaxHighlighter.apply(to: textView, language: currentLanguage)
            isApplyingHighlight = false
        }

        func textDidChange(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            text = textView.string
            caretOffset = textView.selectedRange().location
            lastCaretOffset = caretOffset
            applySyntaxHighlighting()
            ruler?.needsDisplay = true
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            caretOffset = textView.selectedRange().location
            lastCaretOffset = caretOffset
        }
    }
}

private final class CodeLineNumberRulerView: NSRulerView {
    weak var codeTextView: NSTextView?

    init(textView: NSTextView, scrollView: NSScrollView) {
        codeTextView = textView
        super.init(scrollView: scrollView, orientation: .verticalRuler)
        clientView = textView
        ruleThickness = 48
    }

    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func drawHashMarksAndLabels(in rect: NSRect) {
        guard let textView = codeTextView,
              let layoutManager = textView.layoutManager,
              let textContainer = textView.textContainer else { return }
        NSColor.textBackgroundColor.setFill()
        NSBezierPath(rect: bounds).fill()
        NSColor.separatorColor.setFill()
        NSBezierPath(rect: NSRect(x: bounds.maxX - 1, y: rect.minY, width: 1, height: rect.height)).fill()

        let visible = convert(textView.visibleRect, from: textView)
        let glyphRange = layoutManager.glyphRange(forBoundingRect: visible, in: textContainer)
        var currentLine = 1
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular),
            .foregroundColor: NSColor.tertiaryLabelColor
        ]
        layoutManager.enumerateLineFragments(forGlyphRange: NSRange(location: 0, length: layoutManager.numberOfGlyphs)) {
            lineRect, _, _, glyphRangeForLine, _ in
            defer { currentLine += 1 }
            guard NSIntersectionRange(glyphRangeForLine, glyphRange).length > 0 else { return }
            let label = String(currentLine) as NSString
            let size = label.size(withAttributes: attributes)
            let y = lineRect.minY + textView.textContainerOrigin.y + textView.textContainerInset.height
            label.draw(at: NSPoint(x: bounds.width - size.width - 8, y: y), withAttributes: attributes)
        }
    }
}
