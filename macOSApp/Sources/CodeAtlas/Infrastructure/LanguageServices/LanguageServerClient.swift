import Foundation

/// Minimal, process-backed LSP client. It implements JSON-RPC framing, request correlation,
/// full-document synchronization, diagnostics, completion and definition requests.
/// The executable is selected from a fixed configuration and is never launched through a shell.
final class LanguageServerClient {
    typealias DiagnosticHandler = (String, [LanguageDiagnostic]) -> Void
    typealias LogHandler = (String) -> Void

    private struct PendingRequest {
        let continuation: CheckedContinuation<Any?, Error>
        let timeout: DispatchWorkItem
    }

    private let lock = NSRecursiveLock()
    private let writeLock = NSLock()
    private var process: Process?
    private var inputHandle: FileHandle?
    private var outputHandle: FileHandle?
    private var errorHandle: FileHandle?
    private var codec = LSPFrameCodec()
    private var nextID = 0
    private var pending: [Int: PendingRequest] = [:]
    private var activeDocumentURI: String?
    private var documentVersion = 0
    private var stopped = false
    private var workspaceRootURI: String?
    private var workspaceRootName: String?

    var onDiagnostics: DiagnosticHandler?
    var onLog: LogHandler?
    var onExit: ((Int32) -> Void)?
    private(set) var configurationID: String?

    func start(configuration: LanguageServerLaunchConfiguration, workspaceRoot: URL) async throws {
        guard let executableURL = configuration.resolveExecutable() else {
            throw LanguageServerError.executableNotFound(configuration.executable)
        }

        let alreadyRunning = lock.withLock { process?.isRunning == true }
        guard !alreadyRunning else { throw LanguageServerError.alreadyRunning }
        lock.withLock {
            stopped = false
            codec = LSPFrameCodec()
            configurationID = configuration.identifier
            workspaceRootURI = workspaceRoot.standardizedFileURL.absoluteString
            workspaceRootName = workspaceRoot.lastPathComponent
        }

        let child = Process()
        child.executableURL = executableURL
        child.arguments = configuration.arguments
        child.currentDirectoryURL = workspaceRoot
        var environment = ProcessInfo.processInfo.environment
        let standardPaths = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
        environment["PATH"] = (standardPaths + [environment["PATH"] ?? ""]).joined(separator: ":")
        child.environment = environment

        let input = Pipe()
        let output = Pipe()
        let error = Pipe()
        child.standardInput = input
        child.standardOutput = output
        child.standardError = error

        lock.withLock {
            process = child
            inputHandle = input.fileHandleForWriting
            outputHandle = output.fileHandleForReading
            errorHandle = error.fileHandleForReading
        }

        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else {
                handle.readabilityHandler = nil
                return
            }
            self?.receive(data)
        }
        error.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else {
                handle.readabilityHandler = nil
                return
            }
            if let text = String(data: data, encoding: .utf8), !text.isEmpty {
                self?.onLog?(text)
            }
        }
        child.terminationHandler = { [weak self] process in
            guard let self else { return }
            self.finishAllPending(error: LanguageServerError.processExited(process.terminationStatus))
            self.onExit?(process.terminationStatus)
        }

        do {
            try child.run()
        } catch {
            lock.withLock {
                process = nil
                inputHandle = nil
                outputHandle = nil
                errorHandle = nil
            }
            throw LanguageServerError.launchFailed(error.localizedDescription)
        }

        let capabilities: [String: Any] = [
            "textDocument": [
                "completion": ["completionItem": ["snippetSupport": false]],
                "definition": ["dynamicRegistration": false],
                "publishDiagnostics": ["relatedInformation": true],
                "synchronization": ["dynamicRegistration": false, "willSave": false, "didSave": true]
            ],
            "workspace": ["workspaceFolders": true, "configuration": true]
        ]
        let initialize: [String: Any] = [
            "processId": Int(ProcessInfo.processInfo.processIdentifier),
            "clientInfo": ["name": "CodeAtlas", "version": "0.23.0"],
            "rootUri": workspaceRoot.standardizedFileURL.absoluteString,
            "workspaceFolders": [["uri": workspaceRoot.standardizedFileURL.absoluteString, "name": workspaceRoot.lastPathComponent]],
            "capabilities": capabilities,
            "trace": "off"
        ]
        _ = try await request(method: "initialize", params: initialize, timeout: 30)
        try sendNotification(method: "initialized", params: [:])
    }

    func openDocument(url: URL, languageID: String, text: String) async throws {
        let uri = url.standardizedFileURL.absoluteString
        if let previous = activeDocumentURI, previous != uri {
            try sendNotification(method: "textDocument/didClose", params: ["textDocument": ["uri": previous]])
        }
        activeDocumentURI = uri
        documentVersion = 1
        try sendNotification(method: "textDocument/didOpen", params: [
            "textDocument": ["uri": uri, "languageId": languageID, "version": documentVersion, "text": text]
        ])
    }

    func changeDocument(text: String) throws {
        guard let uri = activeDocumentURI else { throw LanguageServerError.noOpenDocument }
        documentVersion += 1
        try sendNotification(method: "textDocument/didChange", params: [
            "textDocument": ["uri": uri, "version": documentVersion],
            "contentChanges": [["text": text]]
        ])
    }

    func saveDocument() throws {
        guard let uri = activeDocumentURI else { throw LanguageServerError.noOpenDocument }
        try sendNotification(method: "textDocument/didSave", params: ["textDocument": ["uri": uri]])
    }

    /// Immediately terminates a process whose initialization is being superseded or cancelled.
    func terminate() {
        let child = lock.withLock { () -> Process? in
            stopped = true
            return process
        }
        finishAllPending(error: LanguageServerError.cancelled)
        if child?.isRunning == true { child?.terminate() }
    }

    func completion(url: URL, line: Int, character: Int) async throws -> [LanguageCompletionItem] {
        let response = try await request(method: "textDocument/completion", params: [
            "textDocument": ["uri": url.standardizedFileURL.absoluteString],
            "position": ["line": max(0, line), "character": max(0, character)],
            "context": ["triggerKind": 1]
        ], timeout: 20)
        let payload: [String: Any]?
        if let dictionary = response as? [String: Any] { payload = dictionary }
        else { payload = nil }
        let rawItems = payload?["items"] as? [[String: Any]] ?? (response as? [[String: Any]]) ?? []
        return rawItems.compactMap(LanguageCompletionItem.init(json:)).sorted {
            if let left = $0.sortText, let right = $1.sortText, left != right { return left < right }
            return $0.label.localizedCaseInsensitiveCompare($1.label) == .orderedAscending
        }
    }

    func definition(url: URL, line: Int, character: Int) async throws -> LanguageDefinitionLocation? {
        let response = try await request(method: "textDocument/definition", params: [
            "textDocument": ["uri": url.standardizedFileURL.absoluteString],
            "position": ["line": max(0, line), "character": max(0, character)]
        ], timeout: 20)
        let location: [String: Any]?
        if let dictionary = response as? [String: Any] { location = dictionary }
        else if let array = response as? [[String: Any]] { location = array.first }
        else { location = nil }
        guard let location,
              let uri = (location["uri"] as? String) ?? (location["targetUri"] as? String),
              let targetURL = URL(string: uri), targetURL.isFileURL else { return nil }
        let range = location["range"] as? [String: Any]
            ?? location["targetSelectionRange"] as? [String: Any]
            ?? location["targetRange"] as? [String: Any] ?? [:]
        let start = range["start"] as? [String: Any] ?? [:]
        return LanguageDefinitionLocation(url: targetURL, line: max(1, (start["line"] as? Int ?? 0) + 1), column: max(1, (start["character"] as? Int ?? 0) + 1))
    }

    func shutdown() async {
        let wasRunning = lock.withLock { () -> Bool in
            stopped = true
            return process?.isRunning == true
        }
        guard wasRunning else { return }
        do { _ = try await request(method: "shutdown", params: [:], timeout: 2) } catch { }
        try? sendNotification(method: "exit", params: [:])
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.25) { [weak self] in
            guard let self else { return }
            self.lock.lock()
            let child = self.process
            self.lock.unlock()
            if child?.isRunning == true { child?.terminate() }
        }
    }

    private func request(method: String, params: [String: Any], timeout seconds: TimeInterval) async throws -> Any? {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Any?, Error>) in
            lock.lock()
            nextID += 1
            let id = nextID
            let timeout = DispatchWorkItem { [weak self] in
                self?.resolve(id: id, result: .failure(LanguageServerError.requestTimedOut(method)))
            }
            pending[id] = PendingRequest(continuation: continuation, timeout: timeout)
            lock.unlock()
            DispatchQueue.global().asyncAfter(deadline: .now() + seconds, execute: timeout)
            do {
                try sendMessage(["jsonrpc": "2.0", "id": id, "method": method, "params": params])
            } catch {
                resolve(id: id, result: .failure(error))
            }
        }
    }

    private func sendNotification(method: String, params: [String: Any]) throws {
        try sendMessage(["jsonrpc": "2.0", "method": method, "params": params])
    }

    private func sendMessage(_ message: [String: Any]) throws {
        guard JSONSerialization.isValidJSONObject(message) else { throw LanguageServerError.invalidJSON }
        let data = try JSONSerialization.data(withJSONObject: message, options: [.fragmentsAllowed])
        let frame = LSPFrameCodec.frame(data)
        lock.lock()
        let handle = inputHandle
        let running = process?.isRunning == true
        lock.unlock()
        guard running, let handle else { throw LanguageServerError.notRunning }
        do { try writeLock.withLock { try handle.write(contentsOf: frame) } }
        catch { throw LanguageServerError.writeFailed(error.localizedDescription) }
    }

    private func receive(_ data: Data) {
        let frames: [Data]
        do {
            frames = try lock.withLock { try codec.append(data) }
        } catch {
            lock.withLock { codec = LSPFrameCodec() }
            onLog?("[LSP] \(error.localizedDescription)\n")
            return
        }
        for frame in frames { processMessage(frame) }
    }

    private func processMessage(_ data: Data) {
        guard let message = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            onLog?("[LSP] Respuesta JSON no válida.\n")
            return
        }
        if let idNumber = message["id"] as? NSNumber, message["method"] == nil {
            let id = idNumber.intValue
            if let errorObject = message["error"] as? [String: Any] {
                let text = (errorObject["message"] as? String) ?? "Error JSON-RPC sin descripción."
                resolve(id: id, result: .failure(LanguageServerError.remoteError(text)))
            } else {
                resolve(id: id, result: .success(message["result"] is NSNull ? nil : message["result"]))
            }
            return
        }
        guard let method = message["method"] as? String else { return }
        let params = message["params"] as? [String: Any] ?? [:]
        if let serverRequestID = message["id"] {
            handleServerRequest(id: serverRequestID, method: method, params: params)
            return
        }
        if method == "textDocument/publishDiagnostics" {
            let uri = params["uri"] as? String ?? ""
            let raw = params["diagnostics"] as? [[String: Any]] ?? []
            let parsed = raw.compactMap { LanguageDiagnostic(json: $0, uri: uri) }
            onDiagnostics?(uri, parsed)
        } else if method == "window/logMessage" || method == "window/showMessage" {
            if let text = params["message"] as? String { onLog?(text + "\n") }
        }
    }

    private func handleServerRequest(id: Any, method: String, params: [String: Any]) {
        let result: Any
        switch method {
        case "workspace/configuration":
            let items = params["items"] as? [Any] ?? []
            result = items.map { _ in NSNull() }
        case "workspace/workspaceFolders":
            if let rootURI = workspaceRootURI {
                result = [["uri": rootURI, "name": workspaceRootName ?? "Workspace"]]
            } else {
                result = NSNull()
            }
        case "window/workDoneProgress/create", "client/registerCapability", "client/unregisterCapability":
            result = NSNull()
        case "workspace/applyEdit":
            // Applying server-proposed edits implicitly would violate the explicit-review workflow.
            result = ["applied": false, "failureReason": "CodeAtlas requires explicit user approval before applying workspace edits."]
        case "workspace/executeCommand":
            result = NSNull()
            onLog?("[LSP] El servidor solicitó ejecutar un comando; CodeAtlas lo ha rechazado por seguridad.\n")
        case "window/showDocument":
            result = ["success": false]
        case "window/showMessageRequest":
            result = NSNull()
        default:
            result = NSNull()
            onLog?("[LSP] Solicitud no soportada del servidor: \(method).\n")
        }
        do {
            try sendMessage(["jsonrpc": "2.0", "id": id, "result": result])
        } catch {
            onLog?("[LSP] No se pudo responder a \(method): \(error.localizedDescription)\n")
        }
    }

    private func resolve(id: Int, result: Result<Any?, Error>) {
        lock.lock()
        let request = pending.removeValue(forKey: id)
        lock.unlock()
        guard let request else { return }
        request.timeout.cancel()
        switch result {
        case .success(let value): request.continuation.resume(returning: value)
        case .failure(let error): request.continuation.resume(throwing: error)
        }
    }

    private func finishAllPending(error: Error) {
        lock.lock()
        let requests = pending
        pending.removeAll()
        lock.unlock()
        for request in requests.values {
            request.timeout.cancel()
            request.continuation.resume(throwing: error)
        }
    }
}

enum LanguageServerError: LocalizedError {
    case executableNotFound(String)
    case alreadyRunning
    case launchFailed(String)
    case notRunning
    case noOpenDocument
    case cancelled
    case invalidJSON
    case writeFailed(String)
    case requestTimedOut(String)
    case remoteError(String)
    case processExited(Int32)

    var errorDescription: String? {
        switch self {
        case .executableNotFound(let name): return "No se encontró el servidor de lenguaje '\(name)' en PATH. Instálalo o configura su ejecutable en el entorno."
        case .alreadyRunning: return "El servidor de lenguaje ya está en ejecución."
        case .launchFailed(let detail): return "No se pudo iniciar el servidor de lenguaje: \(detail)"
        case .notRunning: return "El servidor de lenguaje no está activo."
        case .noOpenDocument: return "No hay un documento abierto en el servidor de lenguaje."
        case .cancelled: return "La sesión del servidor de lenguaje fue cancelada."
        case .invalidJSON: return "No se pudo serializar el mensaje JSON-RPC."
        case .writeFailed(let detail): return "No se pudo escribir al servidor de lenguaje: \(detail)"
        case .requestTimedOut(let method): return "El servidor de lenguaje agotó el tiempo para '\(method)'."
        case .remoteError(let detail): return detail
        case .processExited(let code): return "El servidor de lenguaje terminó con código \(code)."
        }
    }
}
