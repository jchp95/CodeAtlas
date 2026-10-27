import Foundation

enum AIArchitectureContext {
    static func summary(_ snapshot: ArchitectureSnapshot?) -> String {
        guard let snapshot else { return "" }
        let names = Dictionary(snapshot.projects.map { ($0.id, $0.name) }, uniquingKeysWith: { first, _ in first })
        let payload: [String: Any] = [
            "workspace": snapshot.repositoryName,
            "workspaceRoots": Array((snapshot.workspaceRoots ?? []).prefix(30)),
            "branch": snapshot.branch,
            "headCommit": snapshot.headCommit,
            "summary": ["dotnetProjects": snapshot.summary.dotnetProjects, "nodeProjects": snapshot.summary.nodeProjects,
                        "endpoints": snapshot.summary.endpointCandidates, "relations": snapshot.summary.relations],
            "projects": snapshot.projects.prefix(150).map { project in
                ["name": project.name, "kind": project.kind, "path": project.path, "repositoryRoot": project.repositoryRoot ?? "",
                 "framework": project.framework ?? "", "projectReferences": Array(project.projectReferences.prefix(30)),
                 "endpoints": Array(project.endpoints.prefix(25)), "contractsPublished": Array(project.contractsPublished.prefix(30)),
                 "contractsConsumed": Array(project.contractsConsumed.prefix(30)),
                 "evidence": (project.codeEvidence ?? []).prefix(8).map { ["category": $0.category, "symbol": $0.symbol, "file": $0.file, "line": $0.line, "confidence": $0.confidence] as [String: Any] }] as [String: Any]
            },
            "relations": snapshot.relations.prefix(300).map { relation in
                ["source": names[relation.sourceId] ?? relation.sourceId, "target": names[relation.targetId] ?? relation.targetId,
                 "type": relation.type, "label": relation.label, "contract": relation.contract ?? "", "evidence": relation.evidence,
                 "confidence": relation.confidence] as [String: Any]
            },
            "businessFlows": (snapshot.businessFlows ?? []).prefix(60).map { flow in
                ["project": flow.projectName, "name": flow.name, "entryPoint": flow.entryPoint, "confidence": flow.confidence,
                 "summary": flow.summary, "steps": flow.steps.sorted { $0.sequence < $1.sequence }.prefix(12).map { step in
                    ["kind": step.kind, "symbol": step.symbol, "file": step.file, "line": step.line, "confidence": step.confidence] as [String: Any]
                 }] as [String: Any]
            },
            "distributedFlows": (snapshot.distributedFlows ?? []).prefix(80).map { flow in
                ["contract": flow.contract, "producer": flow.producerProjectName, "consumer": flow.consumerProjectName,
                 "producerEntryPoint": flow.producerEntryPoint, "consumerEntryPoint": flow.consumerEntryPoint, "confidence": flow.confidence,
                 "steps": flow.steps.sorted { $0.sequence < $1.sequence }.prefix(16).map { step in
                    ["phase": step.phase, "project": step.projectName, "kind": step.kind, "symbol": step.symbol,
                     "file": step.file, "line": step.line, "confidence": step.confidence] as [String: Any]
                 }] as [String: Any]
            },
            "changes": [
                "summary": Array((snapshot.delta?.summary ?? []).prefix(40)),
                "addedProjects": Array((snapshot.delta?.addedProjects ?? []).prefix(30).map(\.name)),
                "removedProjects": Array((snapshot.delta?.removedProjects ?? []).prefix(30).map(\.name)),
                "changedProjects": Array((snapshot.delta?.changedProjects ?? []).prefix(50).map { ["name": $0.name, "fields": $0.changedFields] as [String: Any] }),
                "addedRelations": Array((snapshot.delta?.addedRelations ?? []).prefix(40).map { "\($0.type): \(names[$0.sourceId] ?? $0.sourceId) -> \(names[$0.targetId] ?? $0.targetId) · \($0.label)" }),
                "removedRelations": Array((snapshot.delta?.removedRelations ?? []).prefix(40).map { "\($0.type): \(names[$0.sourceId] ?? $0.sourceId) -> \(names[$0.targetId] ?? $0.targetId) · \($0.label)" })
            ] as [String: Any],
            "warnings": Array(snapshot.warnings.prefix(30))
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: payload, options: [.prettyPrinted, .sortedKeys]),
              let text = String(data: data, encoding: .utf8) else { return "" }
        return String(text.prefix(30_000))
    }
}
