import Foundation

enum AIProvider: String, CaseIterable, Identifiable {
    case ollama = "Ollama local"
    case anthropic = "Anthropic API"
    var id: String { rawValue }
}
