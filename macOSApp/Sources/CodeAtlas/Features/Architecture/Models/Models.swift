import Foundation

struct ArchitectureSnapshot: Codable {
    var repositoryPath: String
    var repositoryName: String
    var branch: String
    var headCommit: String
    var scannedAt: String
    var projects: [ProjectNode]
    var relations: [ArchitectureRelation]
    var changes: [GitChange]
    var warnings: [String]
    var summary: ScanSummary
    var delta: ArchitectureDelta?
    // Optional fields keep older snapshots readable as the model evolves.
    var businessFlows: [BusinessFlow]? = nil
    var distributedFlows: [DistributedFlow]? = nil
    var gitRepositories: [GitRepositoryState]? = nil
    var workspaceRoots: [String]? = nil
}

struct ProjectNode: Codable, Identifiable {
    var id: String
    var name: String
    var path: String
    var kind: String
    var framework: String?
    var endpoints: [String]
    var dependencies: [String]
    var projectReferences: [String]
    var contractsPublished: [String]
    var contractsConsumed: [String]
    var publishedEvidence: [String]
    var consumedEvidence: [String]
    var httpTargets: [HttpTargetCandidate]
    var listenAddresses: [String]
    var codeEvidence: [ArchitectureEvidence]? = nil
    var repositoryRoot: String? = nil
}

struct BusinessFlow: Codable, Identifiable {
    var id: String
    var projectId: String
    var projectName: String
    var name: String
    var entryPoint: String
    var entryFile: String
    var entryLine: Int
    var confidence: String
    var summary: String
    var steps: [BusinessFlowStep]
}

struct BusinessFlowStep: Codable, Identifiable {
    var sequence: Int
    var kind: String
    var symbol: String
    var file: String
    var line: Int
    var confidence: String
    var detail: String
    var id: String { "\(sequence)|\(kind)|\(symbol)|\(file)|\(line)" }
}

struct FlowChange: Codable, Identifiable {
    var id: String
    var status: String
    var projectId: String
    var projectName: String
    var name: String
    var changes: [String]
    var previousSteps: [BusinessFlowStep]
    var currentSteps: [BusinessFlowStep]
}

struct DistributedFlow: Codable, Identifiable {
    var id: String
    var contract: String
    var producerProjectId: String
    var producerProjectName: String
    var producerFlowId: String?
    var producerEntryPoint: String
    var consumerProjectId: String
    var consumerProjectName: String
    var consumerFlowId: String?
    var consumerEntryPoint: String
    var confidence: String
    var summary: String
    var steps: [DistributedFlowStep]
}

struct DistributedFlowStep: Codable, Identifiable {
    var sequence: Int
    var phase: String
    var projectId: String
    var projectName: String
    var kind: String
    var symbol: String
    var file: String
    var line: Int
    var confidence: String
    var detail: String
    var id: String { "\(sequence)|\(phase)|\(kind)|\(symbol)|\(file)|\(line)" }
}

struct DistributedFlowChange: Codable, Identifiable {
    var id: String
    var status: String
    var contract: String
    var producerProjectName: String
    var consumerProjectName: String
    var producerEntryPoint: String
    var consumerEntryPoint: String
    var changes: [String]
}

struct ArchitectureEvidence: Codable, Identifiable {
    var category: String
    var symbol: String
    var file: String
    var line: Int
    var confidence: String
    var detail: String
    var id: String { "\(category)|\(symbol)|\(file)|\(line)|\(detail)" }
}

struct HttpTargetCandidate: Codable, Identifiable {
    var url: String
    var sourcePath: String
    var id: String { "\(url)|\(sourcePath)" }
}

struct ArchitectureRelation: Codable, Identifiable {
    var id: String
    var sourceId: String
    var targetId: String
    var type: String
    var label: String
    var evidence: String
    var confidence: String
    var contract: String? = nil
}

struct GitChange: Codable, Identifiable {
    var id: String { "\(status):\(path)" }
    var status: String
    var path: String
}

struct GitRepositoryState: Codable, Identifiable {
    var path: String
    var branch: String
    var headCommit: String
    var changes: [GitChange]?
    var id: String { path }
}

struct ScanSummary: Codable {
    var dotnetProjects: Int
    var nodeProjects: Int
    var endpointCandidates: Int
    var changedFiles: Int
    var relations: Int
    var publishedContractCandidates: Int
    var consumedContractCandidates: Int
}

struct ChangedProject: Codable, Identifiable {
    var id: String
    var name: String
    var changedFields: [String]
}

struct ArchitectureDelta: Codable {
    var previousCommit: String
    var currentCommit: String
    var previousBranch: String
    var currentBranch: String
    var addedProjects: [ProjectNode]
    var removedProjects: [ProjectNode]
    var changedProjects: [ChangedProject]
    var addedRelations: [ArchitectureRelation]
    var removedRelations: [ArchitectureRelation]
    var summary: [String]
    var flowChanges: [FlowChange]? = nil
    var distributedFlowChanges: [DistributedFlowChange]? = nil

    var totalChanges: Int {
        addedProjects.count + removedProjects.count + changedProjects.count + addedRelations.count + removedRelations.count
            + (flowChanges?.count ?? 0) + (distributedFlowChanges?.count ?? 0)
    }
}

// HTTP test catalog is loaded on demand and is intentionally excluded from persistent architecture snapshots.
struct HTTPTestingCatalog: Codable {
    var rootPath: String
    var endpoints: [HTTPApiEndpoint]
    var requests: [HTTPTestRequest]
    var httpCalls: [HTTPCallTrace]
    var warnings: [String]
}

struct HTTPApiEndpoint: Codable, Identifiable {
    var id: String
    var projectId: String
    var projectName: String
    var repositoryPath: String
    var workspaceRoot: String? = nil
    var framework: String
    var method: String
    var route: String
    var sourcePath: String
    var line: Int
    var requestType: String?
    var sampleBodyJson: String?
    var schemaSource: String
    var pathParameters: [String]
    var confidence: String
    var detail: String
    var listenAddresses: [String]
}

struct HTTPTestRequest: Codable, Identifiable {
    var id: String
    var projectId: String
    var projectName: String
    var repositoryPath: String
    var workspaceRoot: String? = nil
    var sourcePath: String
    var index: Int
    var name: String
    var method: String
    var url: String
    var headersText: String
    var body: String
    var line: Int
    var associatedEndpointIds: [String]
    var unresolvedVariables: [String]
    var availableEnvironments: [String]
}

struct HTTPRequestOverrides: Codable {
    var method: String?
    var url: String?
    var headersText: String?
    var body: String?
}

struct HTTPExecutionResult: Codable {
    var success: Bool
    var statusCode: Int
    var reasonPhrase: String
    var durationMs: Int64
    var url: String
    var headers: [String: String]
    var body: String
    var bodyTruncated: Bool
    var error: String?
}

struct HTTPCallTrace: Codable, Identifiable {
    var id: String
    var sourceProjectId: String
    var sourceProjectName: String
    var repositoryPath: String
    var workspaceRoot: String? = nil
    var sourcePath: String
    var line: Int
    var method: String
    var route: String
    var symbol: String
    var targetEndpointIds: [String]
    var confidence: String
    var detail: String
}
