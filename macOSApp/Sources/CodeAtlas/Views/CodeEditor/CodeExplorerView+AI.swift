import AppKit
import SwiftUI

extension CodeExplorerView {
    var aiSuggestionSheet: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("Autocompletado con IA", systemImage: "sparkles").font(.title3.bold())
                Spacer()
                Text(aiService.provider.rawValue).font(.caption).foregroundStyle(.secondary)
            }
            Text("La sugerencia se insertará en el cursor solo cuando pulses Insertar. El archivo no se modifica automáticamente.")
                .font(.callout).foregroundStyle(.secondary)
            TextField("Qué debe completar el modelo", text: $aiPrompt, axis: .vertical)
                .textFieldStyle(.roundedBorder).lineLimit(2...4)
            HStack {
                Button { Task { await generateAICodeSuggestion() } } label: {
                    if isGeneratingSuggestion { ProgressView().controlSize(.small) }
                    else { Label("Generar sugerencia", systemImage: "sparkles") }
                }.buttonStyle(.borderedProminent).disabled(isGeneratingSuggestion || selectedFileURL == nil)
                Spacer()
                Button("Cerrar") { showAISuggestion = false }.keyboardShortcut(.cancelAction)
            }
            if !aiSuggestion.isEmpty {
                Divider()
                Text("Propuesta de inserción").font(.headline)
                TextEditor(text: $aiSuggestion).font(.system(size: 11, design: .monospaced))
                    .frame(minHeight: 180).overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.12)))
                HStack {
                    Button("Copiar sugerencia") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(aiSuggestion, forType: .string)
                    }.buttonStyle(.bordered)
                    Spacer()
                    Button("Insertar en el cursor") { insertAISuggestion() }
                        .buttonStyle(.borderedProminent).disabled(aiSuggestion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
        .padding(18).frame(minWidth: 670, minHeight: 430)
    }

    func generateAICodeSuggestion() async {
        guard let selectedFileURL else { return }
        isGeneratingSuggestion = true
        aiSuggestion = ""
        defer { isGeneratingSuggestion = false }
        let source = editorText as NSString
        let offset = min(max(0, caretOffset), source.length)
        let contextStart = max(0, offset - 7000)
        let contextEnd = min(source.length, offset + 7000)
        let prefix = source.substring(with: NSRange(location: contextStart, length: offset - contextStart))
        let suffix = source.substring(with: NSRange(location: offset, length: contextEnd - offset))
        let prompt = """
        Actúa como autocompletado de código para CodeAtlas. Devuelve únicamente el texto que debe insertarse en el punto marcado por <|CURSOR|>. No repitas el código que ya existe a la izquierda o a la derecha, no uses bloques Markdown y no añadas explicación. Sigue el estilo, los tipos y los patrones del archivo. Si faltan datos necesarios, devuelve una sugerencia mínima y deja comentarios explícitos en vez de inventar APIs.

        Archivo: \(selectedFileURL.lastPathComponent)
        Lenguaje detectado: \(languageLabel)
        Instrucción adicional: \(aiPrompt)

        Código alrededor del cursor:
        \(prefix)<|CURSOR|>\(suffix)
        """
        do {
            let answer = try await aiService.ask(question: prompt, architectureContext: "")
            aiSuggestion = Self.stripCodeFence(answer).trimmingCharacters(in: .newlines)
            if aiSuggestion.isEmpty { errorMessage = "El modelo no devolvió una sugerencia de código." }
        } catch { errorMessage = "No se pudo generar la sugerencia: \(error.localizedDescription)" }
    }

    func insertAISuggestion() {
        let source = editorText as NSString
        let offset = min(max(0, caretOffset), source.length)
        let addition = aiSuggestion
        editorText = source.replacingCharacters(in: NSRange(location: offset, length: 0), with: addition)
        caretOffset = offset + (addition as NSString).length
        targetLine = lineNumber(in: editorText, offset: caretOffset)
        showAISuggestion = false
    }

    private static func stripCodeFence(_ source: String) -> String {
        let trimmed = source.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("```") else { return trimmed }
        var lines = trimmed.components(separatedBy: .newlines)
        if !lines.isEmpty { lines.removeFirst() }
        if lines.last?.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("```") == true { lines.removeLast() }
        return lines.joined(separator: "\n")
    }
}
