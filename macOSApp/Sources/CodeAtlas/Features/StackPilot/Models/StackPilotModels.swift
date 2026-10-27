import Foundation

struct StackPilotResponse: Codable {
    var configured: Bool
    var configurationPath: String
    var message: String?
    var error: String?
    var profiles: [StackPilotProfileStatus]
}

struct StackPilotProfileStatus: Codable, Identifiable {
    var id: String
    var name: String
    var workspaceRoot: String
    var services: [StackPilotServiceStatus]
}

struct StackPilotServiceStatus: Codable, Identifiable {
    var id: String
    var name: String
    var kind: String
    var enabled: Bool
    var status: String
    var pid: Int?
    var workingDirectory: String
    var command: String
    var healthUrl: String?
    var healthStatus: String
    var logPath: String
    var dependsOn: [String]
    var error: String?
}

struct StackPilotLogDocument: Identifiable {
    let id = UUID()
    var title: String
    var content: String
}
