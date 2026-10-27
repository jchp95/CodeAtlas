import SwiftUI
import AppKit

struct ArchitectureDiagramLayout {
    static let laneTitles = ["CLIENTES / FRONTEND", "APIs DE NEGOCIO", "WORKERS / CONSUMIDORES", "CAPAS / CONTRATOS", "INFRAESTRUCTURA"]
    static let nodeWidth: CGFloat = 278
    static let nodeHeight: CGFloat = 142
    static let horizontalInset: CGFloat = 20
    static let columnStride: CGFloat = 326
    static let verticalStride: CGFloat = 168

    let nodes: [DiagramNodeLayout]
    let edges: [DiagramEdgeLayout]
    let width: CGFloat
    let height: CGFloat

    init(snapshot: ArchitectureSnapshot) {
        let delta = snapshot.delta
        let addedIDs = Set(delta?.addedProjects.map(\.id) ?? [])
        let changedIDs = Set(delta?.changedProjects.map(\.id) ?? [])
        let currentIDs = Set(snapshot.projects.map(\.id))
        var rawEntries: [(project: ProjectNode, state: DiagramNodeState)] = snapshot.projects.map { project in
            let state: DiagramNodeState = addedIDs.contains(project.id) ? .added : changedIDs.contains(project.id) ? .changed : .normal
            return (project, state)
        }
        for project in delta?.removedProjects ?? [] where !currentIDs.contains(project.id) {
            rawEntries.append((project, .removed))
        }
        let groupedEntries = Self.groupCleanArchitectureLayers(rawEntries)

        func lane(for project: ProjectNode, members: [ProjectNode]) -> Int {
            if project.kind == "Infrastructure" { return 4 }
            if members.contains(where: { $0.kind == "Infrastructure" }) { return 4 }
            if project.kind == "Next.js" || project.kind == "Node.js" { return 0 }
            // A Clean Architecture service is represented by its API/host project. Do not
            // move the whole service into the shared-layer lane merely because members
            // include Domain/Application/Infrastructure projects.
            if members.count > 1 {
                let representative = project.name.lowercased()
                if ["worker", "consumer", "scheduler", "background", "processor"].contains(where: { representative.hasSuffix("." + $0) || representative.hasSuffix("-" + $0) })
                    && project.endpoints.isEmpty { return 2 }
                return 1
            }
            let identity = "\(project.name) \(project.path)".lowercased()
            let sharedTokens = [".domain", "/domain", ".application", "/application", ".infrastructure", "/infrastructure", "contracts", "shared", "common", "abstractions", ".lib", "/lib/"]
            if sharedTokens.contains(where: { identity.contains($0) }) { return 3 }
            let isWorker = ["worker", "consumer", "scheduler", "background", "queue", "processor"].contains(where: { identity.contains($0) })
            if isWorker && project.endpoints.isEmpty { return 2 }
            if project.endpoints.isEmpty && !project.contractsConsumed.isEmpty && project.contractsPublished.isEmpty { return 2 }
            return 1
        }

        var builtNodes: [DiagramNodeLayout] = []
        for laneIndex in 0..<Self.laneTitles.count {
            let laneEntries = groupedEntries.filter { lane(for: $0.project, members: $0.members) == laneIndex }
                .sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
            for (row, entry) in laneEntries.enumerated() {
                let x = Self.horizontalInset + CGFloat(laneIndex) * Self.columnStride
                let y = CGFloat(68) + CGFloat(row) * Self.verticalStride
                builtNodes.append(DiagramNodeLayout(project: entry.project, members: entry.members, displayName: entry.displayName,
                    frame: CGRect(x: x, y: y, width: Self.nodeWidth, height: Self.nodeHeight), lane: laneIndex, state: entry.state))
            }
        }
        nodes = builtNodes
        let blockByProjectID = Dictionary(builtNodes.flatMap { node in node.members.map { ($0.id, node.project.id) } }, uniquingKeysWith: { first, _ in first })
        var builtEdges: [DiagramEdgeLayout] = []
        for relation in snapshot.relations {
            let sourceBlock = blockByProjectID[relation.sourceId] ?? relation.sourceId
            let targetBlock = blockByProjectID[relation.targetId] ?? relation.targetId
            // References between layers of one Clean Architecture service stay inside its block.
            guard sourceBlock != targetBlock else { continue }
            let state: DiagramEdgeState = (delta?.addedRelations.contains(where: { $0.id == relation.id }) == true) ? .added : .normal
            builtEdges.append(DiagramEdgeLayout(relation: relation, state: state, sourceBlockID: sourceBlock, targetBlockID: targetBlock))
        }
        for relation in delta?.removedRelations ?? [] {
            let sourceBlock = blockByProjectID[relation.sourceId] ?? relation.sourceId
            let targetBlock = blockByProjectID[relation.targetId] ?? relation.targetId
            guard sourceBlock != targetBlock else { continue }
            builtEdges.append(DiagramEdgeLayout(relation: relation, state: .removed, sourceBlockID: sourceBlock, targetBlockID: targetBlock))
        }
        let nodeIDs = Set(builtNodes.map(\.project.id))
        edges = builtEdges.filter { nodeIDs.contains($0.sourceBlockID) && nodeIDs.contains($0.targetBlockID) }
        width = Self.horizontalInset * 2 + CGFloat(Self.laneTitles.count - 1) * Self.columnStride + Self.nodeWidth + 24
        let maxRow = builtNodes.map { Int(($0.frame.minY - 68) / Self.verticalStride) }.max() ?? 0
        height = max(440, CGFloat(maxRow + 1) * Self.verticalStride + 124)
    }

    private static func groupCleanArchitectureLayers(_ entries: [(project: ProjectNode, state: DiagramNodeState)]) -> [(project: ProjectNode, members: [ProjectNode], displayName: String, state: DiagramNodeState)] {
        let states = Dictionary(entries.map { ($0.project.id, $0.state) }, uniquingKeysWith: { first, _ in first })
        return CleanArchitectureLayerGrouper.groups(from: entries.map(\.project)).map { group in
            let members = group.members
            let state: DiagramNodeState = members.contains(where: { states[$0.id] == .changed }) ? .changed
                : members.allSatisfy({ states[$0.id] == .removed }) ? .removed
                : members.contains(where: { states[$0.id] == .added }) ? .added : .normal
            return (group.representative, members, group.displayName, state)
        }
    }
}
