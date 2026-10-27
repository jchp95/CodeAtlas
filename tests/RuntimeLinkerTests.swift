import Foundation

@main
struct RuntimeLinkerTests {
    static func main() {
        let root = "/tmp/codeatlas-runtime-linker-fixture"
        let dotnet = ProjectNode(
            id: "src/ZasExpress.Api/ZasExpress.Api.csproj",
            name: "ZasExpress.Api",
            path: "src/ZasExpress.Api/ZasExpress.Api.csproj",
            kind: "Dotnet",
            framework: "net10.0",
            endpoints: [], dependencies: [], projectReferences: [],
            contractsPublished: [], contractsConsumed: [], publishedEvidence: [], consumedEvidence: [],
            httpTargets: [], listenAddresses: []
        )
        let web = ProjectNode(
            id: "apps/portal/package.json",
            name: "@zas/portal",
            path: "apps/portal/package.json",
            kind: "Next.js",
            framework: "Next.js",
            endpoints: [], dependencies: [], projectReferences: [],
            contractsPublished: [], contractsConsumed: [], publishedEvidence: [], consumedEvidence: [],
            httpTargets: [], listenAddresses: []
        )
        let snapshot = ArchitectureSnapshot(
            repositoryPath: root, repositoryName: "fixture", branch: "main", headCommit: "abc123",
            scannedAt: "2026-10-27", projects: [dotnet, web], relations: [], changes: [], warnings: [],
            summary: ScanSummary(dotnetProjects: 1, nodeProjects: 1, endpointCandidates: 0, changedFiles: 0, relations: 0, publishedContractCandidates: 0, consumedContractCandidates: 0),
            delta: nil
        )

        let services = [
            StackPilotServiceStatus(id: "express-api", name: "ZasExpress.Api", kind: "process", enabled: true, status: "running", pid: 123, workingDirectory: root, command: "dotnet run --project src/ZasExpress.Api/ZasExpress.Api.csproj", healthUrl: nil, healthStatus: "not-configured", logPath: "", dependsOn: [], error: nil),
            StackPilotServiceStatus(id: "portal", name: "Portal", kind: "process", enabled: true, status: "stopped", pid: nil, workingDirectory: "\(root)/apps/portal", command: "pnpm run dev", healthUrl: nil, healthStatus: "not-configured", logPath: "", dependsOn: [], error: nil),
            StackPilotServiceStatus(id: "compose-db", name: "Database", kind: "compose", enabled: true, status: "running", pid: nil, workingDirectory: root, command: "docker compose local.yml · db", healthUrl: nil, healthStatus: "not-configured", logPath: "", dependsOn: [], error: nil),
            StackPilotServiceStatus(id: "rabbitmq", name: "Message Broker", kind: "compose", enabled: true, status: "running", pid: nil, workingDirectory: root, command: "docker compose local.yml · rabbitmq", healthUrl: nil, healthStatus: "healthy", logPath: "", dependsOn: [], error: nil),
            StackPilotServiceStatus(id: "minio", name: "MinIO", kind: "compose", enabled: true, status: "stopped", pid: nil, workingDirectory: root, command: "docker compose local.yml · minio", healthUrl: nil, healthStatus: "not-configured", logPath: "", dependsOn: [], error: nil)
        ]
        let response = StackPilotResponse(configured: true, configurationPath: "config.json", message: nil, error: nil, profiles: [
            StackPilotProfileStatus(id: "fixture", name: "Fixture profile", workspaceRoot: "\(root)/.", services: services)
        ])

        let profile = ArchitectureRuntimeLinker.matchingProfile(snapshot: snapshot, workspaceURL: URL(fileURLWithPath: root), response: response)
        expect(profile?.id == "fixture", "profile path normalization")

        let dotnetBinding = ArchitectureRuntimeLinker.binding(for: dotnet, snapshot: snapshot, workspaceURL: URL(fileURLWithPath: root), response: response)
        expect(dotnetBinding?.service.id == "express-api", "dotnet command-to-project binding")
        expect(dotnetBinding?.service.status == "running", "runtime state is preserved")

        let webBinding = ArchitectureRuntimeLinker.binding(for: web, snapshot: snapshot, workspaceURL: URL(fileURLWithPath: root), response: response)
        expect(webBinding?.service.id == "portal", "Node package working-directory binding")
        expect(webBinding?.service.status == "stopped", "stopped state is preserved")

        let ambiguousResponse = StackPilotResponse(configured: true, configurationPath: "config.json", message: nil, error: nil, profiles: [
            StackPilotProfileStatus(id: "fixture", name: "Fixture profile", workspaceRoot: root, services: [services[0], StackPilotServiceStatus(id: "express-api-copy", name: "ZasExpress.Api copy", kind: "process", enabled: true, status: "stopped", pid: nil, workingDirectory: root, command: "dotnet run --project src/ZasExpress.Api/ZasExpress.Api.csproj", healthUrl: nil, healthStatus: "not-configured", logPath: "", dependsOn: [], error: nil)])
        ])
        expect(ArchitectureRuntimeLinker.binding(for: dotnet, snapshot: snapshot, workspaceURL: URL(fileURLWithPath: root), response: ambiguousResponse) == nil, "ambiguous binding fails closed")

        let rootPackage = ProjectNode(
            id: "package.json", name: "root-app", path: "package.json", kind: "Next.js", framework: "Next.js",
            endpoints: [], dependencies: [], projectReferences: [], contractsPublished: [], contractsConsumed: [],
            publishedEvidence: [], consumedEvidence: [], httpTargets: [], listenAddresses: []
        )
        let rootSnapshot = ArchitectureSnapshot(
            repositoryPath: root, repositoryName: "fixture", branch: "main", headCommit: "abc123",
            scannedAt: "2026-10-27", projects: [rootPackage], relations: [], changes: [], warnings: [],
            summary: ScanSummary(dotnetProjects: 0, nodeProjects: 1, endpointCandidates: 0, changedFiles: 0, relations: 0, publishedContractCandidates: 0, consumedContractCandidates: 0),
            delta: nil
        )
        let composeOnly = StackPilotResponse(configured: true, configurationPath: "config.json", message: nil, error: nil, profiles: [
            StackPilotProfileStatus(id: "fixture", name: "Fixture profile", workspaceRoot: root, services: [services[2]])
        ])
        expect(ArchitectureRuntimeLinker.binding(for: rootPackage, snapshot: rootSnapshot, workspaceURL: URL(fileURLWithPath: root), response: composeOnly) == nil, "Compose infrastructure is not mapped to a source project")

        let rabbit = ProjectNode(
            id: "infrastructure:rabbitmq", name: "RabbitMQ", path: ".infrastructure/rabbitmq", kind: "Infrastructure", framework: "docker-compose",
            endpoints: [], dependencies: [], projectReferences: [], contractsPublished: [], contractsConsumed: [],
            publishedEvidence: [], consumedEvidence: [], httpTargets: [], listenAddresses: [], repositoryRoot: root
        )
        let minio = ProjectNode(
            id: "infrastructure:minio", name: "MinIO", path: ".infrastructure/minio", kind: "Infrastructure", framework: "docker-compose",
            endpoints: [], dependencies: [], projectReferences: [], contractsPublished: [], contractsConsumed: [],
            publishedEvidence: [], consumedEvidence: [], httpTargets: [], listenAddresses: [], repositoryRoot: root
        )
        let infrastructureResponse = StackPilotResponse(configured: true, configurationPath: "config.json", message: nil, error: nil, profiles: [
            StackPilotProfileStatus(id: "fixture", name: "Fixture profile", workspaceRoot: root, services: services)
        ])
        let rabbitBinding = ArchitectureRuntimeLinker.binding(for: rabbit, snapshot: rootSnapshot, workspaceURL: URL(fileURLWithPath: root), response: infrastructureResponse)
        expect(rabbitBinding?.service.id == "rabbitmq", "RabbitMQ infrastructure block binds to explicit Compose service")
        let minioBinding = ArchitectureRuntimeLinker.binding(for: minio, snapshot: rootSnapshot, workspaceURL: URL(fileURLWithPath: root), response: infrastructureResponse)
        expect(minioBinding?.service.id == "minio", "MinIO infrastructure block binds to explicit Compose service")

        // Generic compose names should never be guessed as matching infrastructure aliases.
        let genericOnly = StackPilotResponse(configured: true, configurationPath: "config.json", message: nil, error: nil, profiles: [
            StackPilotProfileStatus(id: "fixture", name: "Fixture profile", workspaceRoot: root, services: [services[2]])
        ])
        expect(ArchitectureRuntimeLinker.binding(for: rabbit, snapshot: rootSnapshot, workspaceURL: URL(fileURLWithPath: root), response: genericOnly) == nil,
               "RabbitMQ must not guess a generic Compose service named db")

        let otherRoot = "/tmp/codeatlas-runtime-linker-compose-repo"
        let sharedInfrastructureResponse = StackPilotResponse(configured: true, configurationPath: "config.json", message: nil, error: nil, profiles: [
            StackPilotProfileStatus(id: "app-profile", name: "App profile", workspaceRoot: root, services: [services[0]]),
            StackPilotProfileStatus(id: "infra-profile", name: "Infrastructure profile", workspaceRoot: otherRoot, services: [services[4]])
        ])
        expect(ArchitectureRuntimeLinker.binding(for: minio, snapshot: rootSnapshot, workspaceURL: URL(fileURLWithPath: root), response: sharedInfrastructureResponse)?.profileID == "infra-profile",
               "Shared infrastructure can bind to a unique Compose service in another selected repository profile")

        let duplicateAcrossRoots = StackPilotResponse(configured: true, configurationPath: "config.json", message: nil, error: nil, profiles: [
            StackPilotProfileStatus(id: "infra-a", name: "Infra A", workspaceRoot: root, services: [services[4]]),
            StackPilotProfileStatus(id: "infra-b", name: "Infra B", workspaceRoot: otherRoot, services: [services[4]])
        ])
        expect(ArchitectureRuntimeLinker.binding(for: minio, snapshot: rootSnapshot, workspaceURL: URL(fileURLWithPath: root), response: duplicateAcrossRoots) == nil,
               "Duplicate matching Compose services across roots are ambiguous and fail closed")

        print("Runtime linker tests passed: path normalization, .NET matching, Node matching, ambiguity safety, Compose exclusion for source code, and exact cross-root RabbitMQ/MinIO infrastructure binding.")
    }

    private static func expect(_ condition: Bool, _ label: String) {
        guard condition else {
            fputs("FAIL: \(label)\n", stderr)
            exit(1)
        }
    }
}
