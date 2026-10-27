import Foundation

extension EngineClient {
func stackPilot(arguments: [String]) async throws -> StackPilotResponse {
        let data = try await runEngine(arguments: ["stackpilot"] + arguments)
        do {
            return try JSONDecoder().decode(StackPilotResponse.self, from: data)
        } catch {
            throw EngineError.invalidOutput("StackPilot devolvió JSON no válido: \(error.localizedDescription)")
        }
    }

    func stackPilotLogs(profileID: String, serviceID: String, lines: Int = 200) async throws -> String {
        let data = try await runEngine(arguments: ["stackpilot", "logs", profileID, serviceID, "--lines", String(lines)])
        return String(data: data, encoding: .utf8) ?? "No se pudo interpretar la salida de logs."
    }
}
