import Foundation

@main
struct ModelCompatibilityTests {
    static func main() throws {
        // Snapshots written by 0.5.0 do not have the codeEvidence field.
        let legacyJSON = """
        {
          "id": "src/Example.Api.csproj",
          "name": "Example.Api",
          "path": "src/Example.Api.csproj",
          "kind": "Dotnet",
          "framework": "net10.0",
          "endpoints": [],
          "dependencies": [],
          "projectReferences": [],
          "contractsPublished": [],
          "contractsConsumed": [],
          "publishedEvidence": [],
          "consumedEvidence": [],
          "httpTargets": [],
          "listenAddresses": []
        }
        """
        let project = try JSONDecoder().decode(ProjectNode.self, from: Data(legacyJSON.utf8))
        precondition(project.codeEvidence == nil, "Legacy snapshots should decode without codeEvidence")

        let currentJSON = """
        {
          "id": "src/Example.Api.csproj",
          "name": "Example.Api",
          "path": "src/Example.Api.csproj",
          "kind": "Dotnet",
          "framework": "net10.0",
          "endpoints": [],
          "dependencies": [],
          "projectReferences": [],
          "contractsPublished": [],
          "contractsConsumed": [],
          "publishedEvidence": [],
          "consumedEvidence": [],
          "httpTargets": [],
          "listenAddresses": [],
          "codeEvidence": [{
            "category": "message-published",
            "symbol": "Contracts.CargoRegistered",
            "file": "Program.cs",
            "line": 12,
            "confidence": "resolved",
            "detail": "Publish call"
          }]
        }
        """
        let current = try JSONDecoder().decode(ProjectNode.self, from: Data(currentJSON.utf8))
        precondition(current.codeEvidence?.first?.symbol == "Contracts.CargoRegistered")

        let legacySnapshotJSON = """
        {
          "repositoryPath": "/tmp/legacy",
          "repositoryName": "legacy",
          "branch": "main",
          "headCommit": "abc123",
          "scannedAt": "2026-10-27 10:00:00 -04:00",
          "projects": [],
          "relations": [],
          "changes": [],
          "warnings": [],
          "summary": {
            "dotnetProjects": 0,
            "nodeProjects": 0,
            "endpointCandidates": 0,
            "changedFiles": 0,
            "relations": 0,
            "publishedContractCandidates": 0,
            "consumedContractCandidates": 0
          },
          "delta": null
        }
        """
        let legacySnapshot = try JSONDecoder().decode(ArchitectureSnapshot.self, from: Data(legacySnapshotJSON.utf8))
        precondition(legacySnapshot.businessFlows == nil, "Legacy snapshots should decode without businessFlows")
        precondition(legacySnapshot.distributedFlows == nil, "Legacy snapshots should decode without distributedFlows")
        precondition(legacySnapshot.gitRepositories == nil, "Legacy snapshots should decode without gitRepositories")

        let currentFlowJSON = """
        {
          "id": "flow-1",
          "projectId": "src/Example.Api.csproj",
          "projectName": "Example.Api",
          "name": "POST /api/cargo",
          "entryPoint": "POST /api/cargo",
          "entryFile": "Program.cs",
          "entryLine": 12,
          "confidence": "source-traced",
          "summary": "Endpoint and handler traced",
          "steps": [{
            "sequence": 0,
            "kind": "endpoint",
            "symbol": "POST /api/cargo",
            "file": "Program.cs",
            "line": 12,
            "confidence": "declared",
            "detail": "Route declared"
          }]
        }
        """
        let currentFlow = try JSONDecoder().decode(BusinessFlow.self, from: Data(currentFlowJSON.utf8))
        precondition(currentFlow.steps.count == 1 && currentFlow.steps[0].kind == "endpoint")

        let distributedFlowJSON = """
        {
          "id": "distributed-1",
          "contract": "Zas.Contracts.CargoRegistered",
          "producerProjectId": "src/ZasExpress.Api.csproj",
          "producerProjectName": "ZasExpress.Api",
          "producerFlowId": "flow-1",
          "producerEntryPoint": "POST /api/cargo",
          "consumerProjectId": "src/ZasTrack.Api.csproj",
          "consumerProjectName": "ZasTrack.Api",
          "consumerFlowId": "flow-2",
          "consumerEntryPoint": "CONSUME Zas.Contracts.CargoRegistered",
          "confidence": "inferred",
          "summary": "Static contract match",
          "steps": [{
            "sequence": 0,
            "phase": "transport",
            "projectId": "src/ZasExpress.Api.csproj",
            "projectName": "ZasExpress.Api",
            "kind": "message-transport",
            "symbol": "RabbitMQ / MassTransit · Zas.Contracts.CargoRegistered",
            "file": "Handlers/RegisterCargoCommandHandler.cs",
            "line": 14,
            "confidence": "inferred",
            "detail": "Contract match only"
          }]
        }
        """
        let distributed = try JSONDecoder().decode(DistributedFlow.self, from: Data(distributedFlowJSON.utf8))
        precondition(distributed.steps.first?.phase == "transport", "Distributed message path should preserve its transport phase")
        precondition(distributed.consumerProjectName == "ZasTrack.Api")

        let gitRepositoryJSON = """
        {"path":"services/zas-express-api","branch":"feature/cargo-flow","headCommit":"abc1234","changes":[{"status":"M","path":"services/zas-express-api/Handlers/RegisterCargo.cs"}]}
        """
        let gitRepository = try JSONDecoder().decode(GitRepositoryState.self, from: Data(gitRepositoryJSON.utf8))
        precondition(gitRepository.path == "services/zas-express-api")
        precondition(gitRepository.changes?.first?.path == "services/zas-express-api/Handlers/RegisterCargo.cs")
        var workspaceSnapshot = legacySnapshot
        workspaceSnapshot.gitRepositories = [gitRepository]
        let workspaceSnapshotData = try JSONEncoder().encode(workspaceSnapshot)
        let decodedWorkspaceSnapshot = try JSONDecoder().decode(ArchitectureSnapshot.self, from: workspaceSnapshotData)
        precondition(decodedWorkspaceSnapshot.gitRepositories?.first?.path == "services/zas-express-api")
        let legacyHTTPCallJSON = """
        {"id":"call-1","sourceProjectId":"Api","sourceProjectName":"Api","repositoryPath":"repo","sourcePath":"Program.cs","line":12,"method":"GET","route":"/api/status","symbol":"HttpClient.GetAsync","targetEndpointIds":[],"confidence":"unresolved","detail":"No static target"}
        """
        let legacyCall = try JSONDecoder().decode(HTTPCallTrace.self, from: Data(legacyHTTPCallJSON.utf8))
        precondition(legacyCall.workspaceRoot == nil, "Older HTTP call entries should decode without workspaceRoot")

        print("Model compatibility tests passed: legacy snapshots, local flows, distributed paths, and multi-repository Git state decode.")

    }
}
