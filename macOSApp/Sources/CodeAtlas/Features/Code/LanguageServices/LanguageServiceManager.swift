import Foundation
import Combine

@MainActor
final class LanguageServiceManager: ObservableObject {
    @Published private(set) var status = LanguageServerStatus.unavailable
    @Published private(set) var diagnostics: [LanguageDiagnostic] = []
    @Published private(set) var completionItems: [LanguageCompletionItem] = []
    @Published private(set) var isRequestingCompletion = false
    @Published private(set) var lastMessage: String?
    @Published private(set) var serverLog = ""

    private var client: LanguageServerClient?
    private var currentConfiguration: LanguageServerLaunchConfiguration?
    private var currentRootURL: URL?
    private var currentDocumentURL: URL?
    private var latestText = ""
    private var isDocumentReady = false
    private var generation = 0

    var canUseSemanticFeatures: Bool { status.state == .ready && isDocumentReady }
    var diagnosticErrorCount: Int { diagnostics.filter { $0.severity == 1 }.count }
    var diagnosticWarningCount: Int { diagnostics.filter { $0.severity == 2 }.count }

    func openDocument(url: URL, rootURL: URL, text: String) {
        let normalizedURL = url.standardizedFileURL
        let normalizedRoot = rootURL.standardizedFileURL
        latestText = text
        guard let configuration = LanguageServerLaunchConfiguration.preferred(forFileExtension: normalizedURL.pathExtension) else {
            stop()
            status = LanguageServerStatus(state: .unavailable, serverName: "LSP", message: "No hay adaptador LSP configurado para .\(normalizedURL.pathExtension). El outline heurístico sigue disponible.")
            return
        }
        guard configuration.resolveExecutable() != nil else {
            stop()
            currentConfiguration = configuration
            status = LanguageServerStatus(state: .unavailable, serverName: configuration.identifier, message: "Servidor no instalado; instala '\(configuration.executable)' para activar semántica, diagnósticos y completado.")
            return
        }

        if let client, currentConfiguration == configuration,
           currentRootURL?.path == normalizedRoot.path, isDocumentReady {
            let previousURL = currentDocumentURL
            currentDocumentURL = normalizedURL
            diagnostics = []
            lastMessage = nil
            Task {
                do {
                    if previousURL?.path != normalizedURL.path {
                        try await client.openDocument(url: normalizedURL, languageID: configuration.languageID(forFileExtension: normalizedURL.pathExtension), text: self.latestText)
                    } else {
                        try client.changeDocument(text: self.latestText)
                    }
                } catch {
                    self.lastMessage = error.localizedDescription
                }
            }
            return
        }

        stop()
        generation += 1
        let requestedGeneration = generation
        let newClient = LanguageServerClient()
        client = newClient
        currentConfiguration = configuration
        currentRootURL = normalizedRoot
        currentDocumentURL = normalizedURL
        isDocumentReady = false
        diagnostics = []
        completionItems = []
        lastMessage = nil
        serverLog = ""
        status = LanguageServerStatus(state: .starting, serverName: configuration.identifier, message: "Iniciando servidor de lenguaje…")

        newClient.onDiagnostics = { [weak self] uri, values in
            Task { @MainActor [weak self] in
                guard let self,
                      self.generation == requestedGeneration,
                      self.currentDocumentURL?.standardizedFileURL.absoluteString == uri else { return }
                self.diagnostics = values.sorted {
                    if $0.line != $1.line { return $0.line < $1.line }
                    if $0.column != $1.column { return $0.column < $1.column }
                    return $0.severity < $1.severity
                }
            }
        }
        newClient.onLog = { [weak self] text in
            Task { @MainActor [weak self] in
                guard let self, self.generation == requestedGeneration else { return }
                self.appendLog(text)
            }
        }
        newClient.onExit = { [weak self] exitCode in
            Task { @MainActor [weak self] in
                guard let self, self.generation == requestedGeneration else { return }
                self.isDocumentReady = false
                if self.status.state != .failed {
                    self.status = LanguageServerStatus(state: .failed, serverName: configuration.identifier, message: "El servidor terminó con código \(exitCode). Consulta el registro LSP.")
                }
            }
        }

        Task { [weak self, weak newClient] in
            guard let self, let newClient else { return }
            do {
                try await newClient.start(configuration: configuration, workspaceRoot: normalizedRoot)
                guard self.generation == requestedGeneration else { await newClient.shutdown(); return }
                try await newClient.openDocument(url: normalizedURL, languageID: configuration.languageID(forFileExtension: normalizedURL.pathExtension), text: self.latestText)
                guard self.generation == requestedGeneration else { await newClient.shutdown(); return }
                self.isDocumentReady = true
                self.status = LanguageServerStatus(state: .ready, serverName: configuration.identifier, message: "Semántica LSP activa")
                self.lastMessage = nil
            } catch {
                guard self.generation == requestedGeneration else { return }
                self.isDocumentReady = false
                self.status = LanguageServerStatus(state: .failed, serverName: configuration.identifier, message: error.localizedDescription)
                self.lastMessage = error.localizedDescription
            }
        }
    }

    /// Call after a short UI debounce. Full-text synchronization is used for compatibility across servers.
    func documentChanged(text: String) {
        latestText = text
        guard isDocumentReady, let client else { return }
        do { try client.changeDocument(text: text) }
        catch { lastMessage = error.localizedDescription }
    }

    func documentSaved() {
        guard isDocumentReady, let client else { return }
        do { try client.saveDocument() }
        catch { lastMessage = error.localizedDescription }
    }

    func requestCompletions(text: String, utf16Offset: Int) async {
        guard canUseSemanticFeatures, let client, let document = currentDocumentURL else {
            lastMessage = status.message
            completionItems = []
            return
        }
        let position = Self.lspPosition(text: text, utf16Offset: utf16Offset)
        isRequestingCompletion = true
        defer { isRequestingCompletion = false }
        do {
            completionItems = try await client.completion(url: document, line: position.line, character: position.character)
            lastMessage = completionItems.isEmpty ? "No hay sugerencias para esta posición." : nil
        } catch {
            completionItems = []
            lastMessage = error.localizedDescription
        }
    }

    func goToDefinition(text: String, utf16Offset: Int) async -> LanguageDefinitionLocation? {
        guard canUseSemanticFeatures, let client, let document = currentDocumentURL else {
            lastMessage = status.message
            return nil
        }
        let position = Self.lspPosition(text: text, utf16Offset: utf16Offset)
        do {
            let result = try await client.definition(url: document, line: position.line, character: position.character)
            if result == nil { lastMessage = "El servidor no encontró una definición para la posición actual." }
            else { lastMessage = nil }
            return result
        } catch {
            lastMessage = error.localizedDescription
            return nil
        }
    }

    func stop() {
        generation += 1
        isDocumentReady = false
        if let client {
            if status.state == .starting { client.terminate() }
            else { Task { await client.shutdown() } }
        }
        client = nil
        currentConfiguration = nil
        currentRootURL = nil
        currentDocumentURL = nil
        diagnostics = []
        completionItems = []
        isRequestingCompletion = false
        status = .unavailable
    }

    private func appendLog(_ value: String) {
        serverLog += value
        if serverLog.count > 24_000 {
            serverLog = "[Registro anterior truncado]\n" + String(serverLog.suffix(24_000))
        }
    }

    private static func lspPosition(text: String, utf16Offset: Int) -> (line: Int, character: Int) {
        let source = text as NSString
        let bounded = min(max(0, utf16Offset), source.length)
        let prefix = source.substring(to: bounded) as NSString
        let content = prefix as String
        let line = content.reduce(into: 0) { count, character in if character == "\n" { count += 1 } }
        let lastNewline = source.range(of: "\n", options: .backwards, range: NSRange(location: 0, length: bounded))
        let lastLineStart = lastNewline.location == NSNotFound ? 0 : lastNewline.location + 1
        return (line, max(0, bounded - lastLineStart))
    }
}
