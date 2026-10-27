import Foundation

@main
struct CleanArchitectureLayerGrouperTests {
    static func main() {
        let rootA = "/tmp/repos/zas-express-api"
        let rootB = "/tmp/repos/zas-track-api"
        let projects = [
            make("api", "ZasExpress.Api", root: rootA),
            make("app", "ZasExpress.Application", root: rootA),
            make("domain", "ZasExpress.Domain", root: rootA),
            make("infra", "ZasExpress.Infrastructure", root: rootA),
            make("track-domain", "ZasExpress.Domain", root: rootB),
            make("rabbit", "RabbitMQ", root: rootA, kind: "Infrastructure")
        ]
        let groups = CleanArchitectureLayerGrouper.groups(from: projects)
        let cleanGroup = groups.first { $0.displayName == "ZasExpress" }
        precondition(cleanGroup?.members.count == 4, "Clean Architecture layers in a single repo should appear as one block")
        precondition(cleanGroup?.representative.id == "api", "API project should represent the grouped service when available")
        precondition(!groups.contains { $0.members.count > 1 && $0.members.contains(where: { $0.id == "track-domain" }) },
                     "Projects from different repositories must not merge into one service block")
        precondition(groups.contains { $0.members.count == 1 && $0.members[0].id == "rabbit" },
                     "Infrastructure nodes must remain independent blocks")
        print("Clean Architecture grouping tests passed: layer grouping, API representative, repo isolation, infrastructure singleton.")
    }

    private static func make(_ id: String, _ name: String, root: String, kind: String = "Dotnet") -> ProjectNode {
        ProjectNode(id: id, name: name, path: name + ".csproj", kind: kind, framework: "net10.0", endpoints: [], dependencies: [],
            projectReferences: [], contractsPublished: [], contractsConsumed: [], publishedEvidence: [], consumedEvidence: [],
            httpTargets: [], listenAddresses: [], codeEvidence: nil, repositoryRoot: root)
    }
}
