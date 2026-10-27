import Foundation
import Combine
import Security

@MainActor
final class AIAssistantService: ObservableObject {
    @Published var provider: AIProvider = .ollama
    @Published var ollamaBaseURL = "http://localhost:11434"
    @Published var ollamaModel = ""
    @Published var anthropicModel = ""
    @Published var anthropicKeyDraft = ""
    @Published var hasSavedAnthropicKey = false
    @Published var includeArchitectureSummary = true
    @Published var availableOllamaModels: [String] = []
    @Published var availableAnthropicModels: [String] = []
    @Published var connectionStatus = "Sin comprobar"
    @Published var isCheckingConnection = false
    @Published var lastProviderError: String?

    private let defaults = UserDefaults.standard
    private let keychainService = "com.codeatlas.ai.anthropic"
    private let keychainAccount = "api-key"

    init() {
        provider = AIProvider(rawValue: defaults.string(forKey: "ai.provider") ?? "Ollama local") ?? .ollama
        ollamaBaseURL = defaults.string(forKey: "ai.ollama.baseURL") ?? "http://localhost:11434"
        ollamaModel = defaults.string(forKey: "ai.ollama.model") ?? ""
        anthropicModel = defaults.string(forKey: "ai.anthropic.model") ?? ""
        includeArchitectureSummary = defaults.object(forKey: "ai.includeArchitectureSummary") as? Bool ?? true
        hasSavedAnthropicKey = KeychainSecret.read(service: keychainService, account: keychainAccount) != nil
    }

    func saveSettings() {
        defaults.set(provider.rawValue, forKey: "ai.provider")
        defaults.set(ollamaBaseURL.trimmingCharacters(in: .whitespacesAndNewlines), forKey: "ai.ollama.baseURL")
        defaults.set(ollamaModel.trimmingCharacters(in: .whitespacesAndNewlines), forKey: "ai.ollama.model")
        defaults.set(anthropicModel.trimmingCharacters(in: .whitespacesAndNewlines), forKey: "ai.anthropic.model")
        defaults.set(includeArchitectureSummary, forKey: "ai.includeArchitectureSummary")
        if !anthropicKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            do {
                try KeychainSecret.save(anthropicKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines), service: keychainService, account: keychainAccount)
                hasSavedAnthropicKey = true
                anthropicKeyDraft = ""
            } catch {
                lastProviderError = "No se pudo guardar la clave en el llavero de macOS: \(error.localizedDescription)"
            }
        }
        connectionStatus = "Configuración guardada"
    }

    func removeAnthropicKey() {
        KeychainSecret.delete(service: keychainService, account: keychainAccount)
        hasSavedAnthropicKey = false
        anthropicKeyDraft = ""
        connectionStatus = "Clave eliminada del llavero"
    }

    func testConnection() async {
        isCheckingConnection = true
        defer { isCheckingConnection = false }
        lastProviderError = nil
        do {
            switch provider {
            case .ollama:
                let models = try await fetchOllamaModels()
                availableOllamaModels = models
                connectionStatus = "Ollama disponible · \(models.count) modelo(s) local(es)"
                if ollamaModel.isEmpty, let first = models.first { ollamaModel = first }
            case .anthropic:
                guard let key = KeychainSecret.read(service: keychainService, account: keychainAccount), !key.isEmpty else {
                    throw AIServiceError.configuration("Guarda primero una clave de Anthropic en el llavero.")
                }
                let ids = try await fetchAnthropicModels(apiKey: key)
                availableAnthropicModels = ids.sorted()
                if anthropicModel.isEmpty {
                    anthropicModel = ids.first(where: { $0.localizedCaseInsensitiveContains("sonnet") }) ?? ids.first ?? ""
                }
                connectionStatus = "Anthropic conectado · \(ids.count) modelo(s) visibles en la API"
            }
        } catch {
            connectionStatus = "No conectado"
            lastProviderError = error.localizedDescription
        }
    }

    func ask(question: String, architectureContext: String) async throws -> String {
        let prompt = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !prompt.isEmpty else { throw AIServiceError.configuration("Escribe una pregunta antes de enviar.") }
        let contextBlock = architectureContext.isEmpty ? "" : "\n\nContexto estructurado de CodeAtlas (análisis estático, no telemetría runtime):\n\(architectureContext)"
        let userMessage = prompt + contextBlock
        switch provider {
        case .ollama:
            let model = ollamaModel.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !model.isEmpty else { throw AIServiceError.configuration("Selecciona un modelo local de Ollama en Configuración IA.") }
            let base = try normalizedBaseURL(ollamaBaseURL)
            let url = base.appendingPathComponent("api/chat")
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.timeoutInterval = 180
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: [
                "model": model,
                "stream": false,
                "messages": [
                    ["role": "system", "content": "Eres un asistente de ingeniería de software integrado en CodeAtlas. Responde en español, distingue evidencias de inferencias, no inventes archivos ni relaciones, y propone pasos verificables."],
                    ["role": "user", "content": userMessage]
                ],
                "options": ["temperature": 0.2]
            ])
            let (data, response) = try await URLSession.shared.data(for: request)
            try validateHTTP(response, data: data)
            let decoded = try JSONDecoder().decode(OllamaChatResponse.self, from: data)
            return decoded.message.content
        case .anthropic:
            let model = anthropicModel.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !model.isEmpty else { throw AIServiceError.configuration("Introduce el identificador de modelo de Anthropic en Configuración IA.") }
            guard let key = KeychainSecret.read(service: keychainService, account: keychainAccount), !key.isEmpty else {
                throw AIServiceError.configuration("Guarda una clave de Anthropic en el llavero de macOS.")
            }
            guard let url = URL(string: "https://api.anthropic.com/v1/messages") else { throw AIServiceError.configuration("URL de Anthropic no válida.") }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.timeoutInterval = 180
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue(key, forHTTPHeaderField: "x-api-key")
            request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
            request.httpBody = try JSONSerialization.data(withJSONObject: [
                "model": model,
                "max_tokens": 1800,
                "system": "Eres un asistente de ingeniería de software integrado en CodeAtlas. Responde en español. Distingue lo confirmado por el código de lo inferido; no inventes rutas, contratos ni servicios. Prioriza explicaciones prácticas y pruebas verificables.",
                "messages": [["role": "user", "content": userMessage]]
            ])
            let (data, response) = try await URLSession.shared.data(for: request)
            try validateHTTP(response, data: data)
            let decoded = try JSONDecoder().decode(AnthropicMessageResponse.self, from: data)
            return decoded.content.compactMap { $0.text }.joined(separator: "\n")
        }
    }

    private func fetchOllamaModels() async throws -> [String] {
        let base = try normalizedBaseURL(ollamaBaseURL)
        let url = base.appendingPathComponent("api/tags")
        var request = URLRequest(url: url)
        request.timeoutInterval = 8
        let (data, response) = try await URLSession.shared.data(for: request)
        try validateHTTP(response, data: data)
        return try JSONDecoder().decode(OllamaTagsResponse.self, from: data).models.map(\.name).sorted()
    }

    private func fetchAnthropicModels(apiKey: String) async throws -> [String] {
        guard let url = URL(string: "https://api.anthropic.com/v1/models?limit=20") else {
            throw AIServiceError.configuration("URL de Anthropic no válida.")
        }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.timeoutInterval = 15
        request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        let (data, response) = try await URLSession.shared.data(for: request)
        try validateHTTP(response, data: data)
        return try JSONDecoder().decode(AnthropicModelsResponse.self, from: data).data.map(\.id)
    }

    private func normalizedBaseURL(_ value: String) throws -> URL {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard let url = URL(string: trimmed), ["http", "https"].contains(url.scheme?.lowercased() ?? ""), url.host != nil else {
            throw AIServiceError.configuration("URL base no válida. Usa, por ejemplo, http://localhost:11434.")
        }
        return url
    }

    private func validateHTTP(_ response: URLResponse, data: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw AIServiceError.network("La respuesta no es HTTP.") }
        guard (200..<300).contains(http.statusCode) else {
            let body = String(data: data, encoding: .utf8) ?? ""
            throw AIServiceError.network("HTTP \(http.statusCode): \(String(body.prefix(900)))")
        }
    }
}

private enum AIServiceError: LocalizedError {
    case configuration(String)
    case network(String)
    var errorDescription: String? {
        switch self { case .configuration(let value), .network(let value): return value }
    }
}

private struct OllamaTagsResponse: Decodable { var models: [OllamaModel] }
private struct OllamaModel: Decodable { var name: String }
private struct OllamaChatResponse: Decodable { var message: OllamaMessage }
private struct OllamaMessage: Decodable { var content: String }
private struct AnthropicModelsResponse: Decodable { var data: [AnthropicModel] }
private struct AnthropicModel: Decodable { var id: String }
private struct AnthropicMessageResponse: Decodable { var content: [AnthropicContent] }
private struct AnthropicContent: Decodable { var type: String; var text: String? }

private enum KeychainSecret {
    static func save(_ value: String, service: String, account: String) throws {
        let data = Data(value.utf8)
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account]
        SecItemDelete(base as CFDictionary)
        var item = base
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(item as CFDictionary, nil)
        guard status == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status), userInfo: [NSLocalizedDescriptionKey: "Keychain status \(status)"]) }
    }

    static func read(service: String, account: String) -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account,
            kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func delete(service: String, account: String) {
        SecItemDelete([kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account] as CFDictionary)
    }
}

// MARK: - AI Settings screen
