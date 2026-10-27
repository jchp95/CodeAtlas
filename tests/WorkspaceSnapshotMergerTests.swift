import Foundation

@main
struct WorkspaceSnapshotMergerTests {
    static func main() throws {
        let expressRoot = URL(fileURLWithPath: "/tmp/codeatlas-workspace/zas-express-api", isDirectory: true)
        let trackRoot = URL(fileURLWithPath: "/tmp/codeatlas-workspace/zas-track-api", isDirectory: true)

        let contract = "Zas.Contracts.CargoRegistered"
        let express = project(id: "src/ZasExpress.Api.csproj", name: "ZasExpress.Api", path: "src/ZasExpress.Api.csproj",
            published: [contract], consumed: [], kind: "Dotnet")
        let expressInfra = project(id: "infrastructure:rabbitmq", name: "RabbitMQ", path: ".infrastructure/rabbitmq",
            published: [], consumed: [], kind: "Infrastructure")
        let track = project(id: "src/ZasTrack.Api.csproj", name: "ZasTrack.Api", path: "src/ZasTrack.Api.csproj",
            published: [], consumed: [contract], kind: "Dotnet")
        let trackInfra = project(id: "infrastructure:rabbitmq", name: "RabbitMQ", path: ".infrastructure/rabbitmq",
            published: [], consumed: [], kind: "Infrastructure")

        let expressSnapshot = snapshot(root: expressRoot, name: "zas-express-api", projects: [express, expressInfra], relations: [
            ArchitectureRelation(id: "express-rabbit", sourceId: express.id, targetId: expressInfra.id, type: "infrastructure", label: "RabbitMQ", evidence: "MassTransit", confidence: "inferred")
        ])
        let trackSnapshot = snapshot(root: trackRoot, name: "zas-track-api", projects: [track, trackInfra], relations: [
            ArchitectureRelation(id: "track-rabbit", sourceId: track.id, targetId: trackInfra.id, type: "infrastructure", label: "RabbitMQ", evidence: "MassTransit", confidence: "inferred")
        ])

        guard let merged = WorkspaceSnapshotMerger.merge([(root: expressRoot, snapshot: expressSnapshot), (root: trackRoot, snapshot: trackSnapshot)]) else {
            fatalError("Expected merged snapshot")
        }
        precondition(merged.workspaceRoots?.count == 2, "Both roots should be present in the workspace snapshot")
        precondition(merged.projects.filter { $0.kind == "Infrastructure" }.count == 1, "Shared infrastructure should deduplicate across roots")
        precondition(merged.projects.filter { $0.kind == "Dotnet" }.count == 2, "Projects from both repos should be retained")
        precondition(Set(merged.projects.filter { $0.kind == "Dotnet" }.compactMap(\.repositoryRoot)).count == 2, "Each project should retain its owning repository root")
        precondition(merged.relations.contains { $0.type == "message-flow" && $0.contract == contract }, "Matching publish/consume contracts should create a cross-repository relation")
        precondition(merged.distributedFlows?.contains { $0.contract == contract && $0.producerProjectName.contains("zas-express-api") && $0.consumerProjectName.contains("zas-track-api") } == true,
                     "Cross-repository distributed flow should identify producer and consumer repositories")
        precondition(Set(merged.projects.map(\.id)).count == merged.projects.count, "Merged project IDs must be unique")
        print("Workspace merger tests passed: multi-root IDs, infrastructure deduplication, contract linking, and distributed flow labels.")
    }

    private static func project(id: String, name: String, path: String, published: [String], consumed: [String], kind: String) -> ProjectNode {
        ProjectNode(id: id, name: name, path: path, kind: kind, framework: kind == "Dotnet" ? "net10.0" : nil,
            endpoints: [], dependencies: [], projectReferences: [], contractsPublished: published, contractsConsumed: consumed,
            publishedEvidence: published.map { "\($0)|Handlers/Publish.cs:17" }, consumedEvidence: consumed.map { "\($0)|Consumers/Consume.cs:22" },
            httpTargets: [], listenAddresses: [], codeEvidence: nil, repositoryRoot: nil)
    }

    private static func snapshot(root: URL, name: String, projects: [ProjectNode], relations: [ArchitectureRelation]) -> ArchitectureSnapshot {
        ArchitectureSnapshot(repositoryPath: root.path, repositoryName: name, branch: "main", headCommit: "abc123", scannedAt: "2026-10-27T12:00:00Z",
            projects: projects, relations: relations, changes: [], warnings: [],
            summary: ScanSummary(dotnetProjects: 1, nodeProjects: 0, endpointCandidates: 0, changedFiles: 0, relations: relations.count,
                publishedContractCandidates: projects.reduce(0) { $0 + $1.contractsPublished.count },
                consumedContractCandidates: projects.reduce(0) { $0 + $1.contractsConsumed.count }),
            delta: nil, businessFlows: [], distributedFlows: [], gitRepositories: [], workspaceRoots: [root.path])
    }
}
