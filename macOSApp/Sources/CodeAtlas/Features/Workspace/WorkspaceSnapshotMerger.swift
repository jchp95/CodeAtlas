import Foundation

/// Combines independently selected repository roots into one architecture snapshot.
/// IDs are prefixed per root to avoid collisions; infrastructure nodes are shared across roots.
enum WorkspaceSnapshotMerger {
    static func merge(_ entries: [(root: URL, snapshot: ArchitectureSnapshot)]) -> ArchitectureSnapshot? {
        guard !entries.isEmpty else { return nil }
        if entries.count == 1 {
            var snapshot = entries[0].snapshot
            snapshot.workspaceRoots = [entries[0].root.standardizedFileURL.path]
            snapshot.projects = snapshot.projects.map { project in
                var copy = project
                copy.repositoryRoot = entries[0].root.standardizedFileURL.path
                return copy
            }
            return snapshot
        }

        let roots = entries.map { $0.root.standardizedFileURL }
        let rootLabels = Dictionary(uniqueKeysWithValues: roots.map { ($0.path, label(for: $0)) })
        let prefixByRoot = Dictionary(uniqueKeysWithValues: roots.map { ($0.path, stablePrefix($0.path)) })
        var projectIDMaps: [String: [String: String]] = [:]
        var flowIDMaps: [String: [String: String]] = [:]

        for entry in entries {
            let root = entry.root.standardizedFileURL
            let prefix = prefixByRoot[root.path] ?? stablePrefix(root.path)
            var projectMap: [String: String] = [:]
            for project in entry.snapshot.projects {
                projectMap[project.id] = canonicalProjectID(project, prefix: prefix)
            }
            projectIDMaps[root.path] = projectMap
            flowIDMaps[root.path] = Dictionary(uniqueKeysWithValues: (entry.snapshot.businessFlows ?? []).map { ($0.id, "\(prefix)::flow::\($0.id)") })
        }

        func projectID(_ id: String, root: URL, kind: String? = nil, name: String? = nil) -> String {
            if kind == "Infrastructure", let name {
                return infrastructureID(name)
            }
            return projectIDMaps[root.standardizedFileURL.path]?[id] ?? "\(stablePrefix(root.path))::\(id)"
        }
        func relation(_ relation: ArchitectureRelation, root: URL) -> ArchitectureRelation {
            var copy = relation
            copy.id = "\(stablePrefix(root.path))::relation::\(relation.id)"
            copy.sourceId = projectIDMaps[root.standardizedFileURL.path]?[relation.sourceId] ?? "\(stablePrefix(root.path))::\(relation.sourceId)"
            // Deduplicate infra targets/sources across repositories.
            if relation.targetId.hasPrefix("infrastructure:") { copy.targetId = relation.targetId }
            else { copy.targetId = projectIDMaps[root.standardizedFileURL.path]?[relation.targetId] ?? "\(stablePrefix(root.path))::\(relation.targetId)" }
            if relation.sourceId.hasPrefix("infrastructure:") { copy.sourceId = relation.sourceId }
            return copy
        }

        var projects: [ProjectNode] = []
        var projectSeen = Set<String>()
        var relations: [ArchitectureRelation] = []
        var businessFlows: [BusinessFlow] = []
        var distributedFlows: [DistributedFlow] = []
        var gitRepositories: [GitRepositoryState] = []
        var changes: [GitChange] = []
        var warnings: [String] = []
        var deltas: [ArchitectureDelta] = []

        for entry in entries {
            let root = entry.root.standardizedFileURL
            let label = rootLabels[root.path] ?? root.lastPathComponent
            let prefix = prefixByRoot[root.path] ?? stablePrefix(root.path)
            let projectMap = projectIDMaps[root.path] ?? [:]
            let flowMap = flowIDMaps[root.path] ?? [:]

            for original in entry.snapshot.projects {
                var copy = original
                copy.id = projectID(original.id, root: root, kind: original.kind, name: original.name)
                copy.repositoryRoot = root.path
                if copy.kind == "Infrastructure" {
                    if projectSeen.insert(copy.id).inserted { projects.append(copy) }
                } else if projectSeen.insert(copy.id).inserted {
                    projects.append(copy)
                }
            }

            relations.append(contentsOf: entry.snapshot.relations.map { relation($0, root: root) })

            for original in entry.snapshot.businessFlows ?? [] {
                var copy = original
                copy.id = flowMap[original.id] ?? "\(prefix)::flow::\(original.id)"
                copy.projectId = projectMap[original.projectId] ?? "\(prefix)::\(original.projectId)"
                copy.projectName = "\(label) · \(original.projectName)"
                businessFlows.append(copy)
            }

            for original in entry.snapshot.distributedFlows ?? [] {
                var copy = original
                copy.id = "\(prefix)::distributed::\(original.id)"
                copy.producerProjectId = projectMap[original.producerProjectId] ?? "\(prefix)::\(original.producerProjectId)"
                copy.consumerProjectId = projectMap[original.consumerProjectId] ?? "\(prefix)::\(original.consumerProjectId)"
                copy.producerProjectName = "\(label) · \(original.producerProjectName)"
                copy.consumerProjectName = "\(label) · \(original.consumerProjectName)"
                if let id = original.producerFlowId { copy.producerFlowId = flowMap[id] ?? "\(prefix)::flow::\(id)" }
                if let id = original.consumerFlowId { copy.consumerFlowId = flowMap[id] ?? "\(prefix)::flow::\(id)" }
                copy.steps = original.steps.map { step in
                    var mapped = step
                    mapped.projectId = projectMap[step.projectId] ?? "\(prefix)::\(step.projectId)"
                    mapped.projectName = "\(label) · \(step.projectName)"
                    return mapped
                }
                distributedFlows.append(copy)
            }

            let mappedChanges = entry.snapshot.changes.map { GitChange(status: $0.status, path: "\(label)/\($0.path)") }
            changes.append(contentsOf: mappedChanges)
            for repository in entry.snapshot.gitRepositories ?? [] {
                var mapped = repository
                mapped.path = "\(label)/\(repository.path)"
                mapped.changes = repository.changes?.map { GitChange(status: $0.status, path: "\(label)/\($0.path)") }
                gitRepositories.append(mapped)
            }
            warnings.append(contentsOf: entry.snapshot.warnings.map { "\(label): \($0)" })
            if let delta = entry.snapshot.delta { deltas.append(delta) }
        }

        addCrossRepositoryMessageRelations(projects: projects, relations: &relations, businessFlows: businessFlows, distributedFlows: &distributedFlows)
        relations = relations.reduce(into: [String: ArchitectureRelation]()) { result, item in result[item.id] = item }.values.sorted { $0.id < $1.id }
        distributedFlows = distributedFlows.reduce(into: [String: DistributedFlow]()) { result, item in result[item.id] = item }.values.sorted { $0.producerProjectName < $1.producerProjectName }

        let rootsDescription = roots.map { label(for: $0) }.joined(separator: " + ")
        let branch = "workspace (\(roots.count) repos)"
        let commitMaterial = entries.map { "\($0.root.standardizedFileURL.path)|\($0.snapshot.branch)|\($0.snapshot.headCommit)" }.joined(separator: "\n")
        let head = "workspace@\(stablePrefix(commitMaterial))"
        let summary = ScanSummary(
            dotnetProjects: projects.filter { $0.kind == "Dotnet" }.count,
            nodeProjects: projects.filter { ["Next.js", "Node.js"].contains($0.kind) }.count,
            endpointCandidates: projects.reduce(0) { $0 + $1.endpoints.count },
            changedFiles: changes.count,
            relations: relations.count,
            publishedContractCandidates: projects.reduce(0) { $0 + $1.contractsPublished.count },
            consumedContractCandidates: projects.reduce(0) { $0 + $1.contractsConsumed.count }
        )
        let delta = mergeDeltas(deltas, roots: entries, projectIDMaps: projectIDMaps, flowIDMaps: flowIDMaps)
        return ArchitectureSnapshot(
            repositoryPath: roots.map(\.path).joined(separator: ";"),
            repositoryName: rootsDescription,
            branch: branch,
            headCommit: head,
            scannedAt: entries.map { $0.snapshot.scannedAt }.max() ?? "",
            projects: projects.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending },
            relations: relations,
            changes: changes,
            warnings: Array(Set(warnings)).sorted(),
            summary: summary,
            delta: delta,
            businessFlows: businessFlows.sorted { $0.projectName.localizedCaseInsensitiveCompare($1.projectName) == .orderedAscending },
            distributedFlows: distributedFlows,
            gitRepositories: gitRepositories,
            workspaceRoots: roots.map(\.path)
        )
    }

    static func stablePrefix(_ value: String) -> String {
        // Stable FNV-1a key; used only to namespace local identifiers, not for security.
        var hash: UInt64 = 14695981039346656037
        for byte in value.utf8 { hash = (hash ^ UInt64(byte)) &* 1099511628211 }
        return String(format: "%08llx", hash & 0xffffffff)
    }

    static func label(for root: URL) -> String {
        let components = root.standardizedFileURL.pathComponents.filter { $0 != "/" }
        return components.suffix(2).joined(separator: "/")
    }

    private static func canonicalProjectID(_ project: ProjectNode, prefix: String) -> String {
        project.kind == "Infrastructure" ? infrastructureID(project.name) : "\(prefix)::\(project.id)"
    }

    private static func infrastructureID(_ name: String) -> String {
        "infrastructure:" + name.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    private static func contractKey(_ value: String) -> String {
        value.replacingOccurrences(of: "global::", with: "")
            .replacingOccurrences(of: " ", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func addCrossRepositoryMessageRelations(
        projects: [ProjectNode], relations: inout [ArchitectureRelation], businessFlows: [BusinessFlow], distributedFlows: inout [DistributedFlow]
    ) {
        func qualifiedName(_ project: ProjectNode) -> String {
            guard let root = project.repositoryRoot, !root.isEmpty else { return project.name }
            return "\(label(for: URL(fileURLWithPath: root, isDirectory: true))) · \(project.name)"
        }
        var existing = Set(relations.filter { $0.type == "message-flow" }.map { "\($0.sourceId)|\($0.targetId)|\(contractKey($0.contract ?? ""))" })
        let publishers = projects.filter { $0.kind != "Infrastructure" && !$0.contractsPublished.isEmpty }
        let consumers = projects.filter { $0.kind != "Infrastructure" && !$0.contractsConsumed.isEmpty }
        for producer in publishers {
            for contract in producer.contractsPublished {
                let key = contractKey(contract)
                for consumer in consumers where consumer.id != producer.id {
                    guard let matched = consumer.contractsConsumed.first(where: { contractKey($0) == key }) else { continue }
                    let identity = "\(producer.id)|\(consumer.id)|\(key)"
                    guard existing.insert(identity).inserted else { continue }
                    let id = "cross-message-\(stablePrefix(identity))"
                    let sourceEvidence = producer.publishedEvidence.first(where: { contractKey($0.components(separatedBy: "|").first ?? "") == key }) ?? producer.path
                    let targetEvidence = consumer.consumedEvidence.first(where: { contractKey($0.components(separatedBy: "|").first ?? "") == contractKey(matched) }) ?? consumer.path
                    relations.append(ArchitectureRelation(id: id, sourceId: producer.id, targetId: consumer.id,
                        type: "message-flow", label: "RabbitMQ / MassTransit · \(contract.components(separatedBy: ".").last ?? contract)",
                        evidence: "Coincidencia de contrato entre repositorios seleccionados. Productor: \(sourceEvidence); consumidor: \(targetEvidence). La publicación/entrega real no se confirma con análisis estático.", confidence: "inferred", contract: contract))

                    let producerFlow = businessFlows.first { $0.projectId == producer.id && $0.steps.contains { $0.kind == "message-publish" && contractKey($0.symbol) == key } }
                    let consumerFlow = businessFlows.first { $0.projectId == consumer.id && $0.steps.contains { $0.kind == "message-consumer" && contractKey($0.symbol) == key } }
                    var steps: [DistributedFlowStep] = []
                    if let producerFlow {
                        for step in producerFlow.steps.sorted(by: { $0.sequence < $1.sequence }) {
                            steps.append(DistributedFlowStep(sequence: steps.count, phase: "producer", projectId: producer.id, projectName: producerFlow.projectName,
                                kind: step.kind, symbol: step.symbol, file: step.file, line: step.line, confidence: step.confidence, detail: step.detail))
                        }
                    } else {
                        let evidence = producer.codeEvidence?.first(where: { $0.category == "message-published" && contractKey($0.symbol) == key })
                        steps.append(DistributedFlowStep(sequence: 0, phase: "producer", projectId: producer.id, projectName: producer.name,
                            kind: "message-publish", symbol: contract, file: evidence?.file ?? producer.path, line: evidence?.line ?? 1,
                            confidence: evidence?.confidence ?? "candidate", detail: "Contrato de publicación detectado en este repositorio."))
                    }
                    steps.append(DistributedFlowStep(sequence: steps.count, phase: "transport", projectId: producer.id, projectName: "RabbitMQ / MassTransit",
                        kind: "message-transport", symbol: contract, file: sourceEvidence, line: 1, confidence: "inferred",
                        detail: "Enlace estático entre repositorios por contrato. Exchange, routing key, retries y entrega efectiva requieren configuración o telemetría."))
                    if let consumerFlow {
                        for step in consumerFlow.steps.sorted(by: { $0.sequence < $1.sequence }) {
                            steps.append(DistributedFlowStep(sequence: steps.count, phase: "consumer", projectId: consumer.id, projectName: consumerFlow.projectName,
                                kind: step.kind, symbol: step.symbol, file: step.file, line: step.line, confidence: step.confidence, detail: step.detail))
                        }
                    } else {
                        let evidence = consumer.codeEvidence?.first(where: { $0.category == "message-consumed" && contractKey($0.symbol) == key })
                        steps.append(DistributedFlowStep(sequence: steps.count, phase: "consumer", projectId: consumer.id, projectName: qualifiedName(consumer),
                            kind: "message-consumer", symbol: contract, file: evidence?.file ?? consumer.path, line: evidence?.line ?? 1,
                            confidence: evidence?.confidence ?? "candidate", detail: "Contrato consumido detectado en el repositorio receptor."))
                    }
                    let flowID = "cross-distributed-\(stablePrefix(identity))"
                    if !distributedFlows.contains(where: { $0.id == flowID }) {
                        distributedFlows.append(DistributedFlow(id: flowID, contract: contract, producerProjectId: producer.id,
                            producerProjectName: qualifiedName(producer), producerFlowId: producerFlow?.id, producerEntryPoint: producerFlow?.name ?? "PUBLISH \(contract.components(separatedBy: ".").last ?? contract)",
                            consumerProjectId: consumer.id, consumerProjectName: qualifiedName(consumer), consumerFlowId: consumerFlow?.id,
                            consumerEntryPoint: consumerFlow?.name ?? "CONSUME \(contract.components(separatedBy: ".").last ?? contract)", confidence: "inferred",
                            summary: "Contrato coincidente entre repositorios seleccionados. Revisa configuración MassTransit y bindings para confirmar el flujo runtime.", steps: steps))
                    }
                }
            }
        }
    }

    private static func mergeDeltas(_ deltas: [ArchitectureDelta], roots: [(root: URL, snapshot: ArchitectureSnapshot)],
                                    projectIDMaps: [String: [String: String]], flowIDMaps: [String: [String: String]]) -> ArchitectureDelta? {
        guard !deltas.isEmpty else { return nil }
        func mapProject(_ project: ProjectNode, root: URL) -> ProjectNode {
            var copy = project
            copy.id = projectIDMaps[root.standardizedFileURL.path]?[project.id] ?? "\(stablePrefix(root.path))::\(project.id)"
            copy.repositoryRoot = root.standardizedFileURL.path
            return copy
        }
        func mapRelation(_ relation: ArchitectureRelation, root: URL) -> ArchitectureRelation {
            var copy = relation
            copy.id = "\(stablePrefix(root.path))::relation::\(relation.id)"
            copy.sourceId = projectIDMaps[root.standardizedFileURL.path]?[relation.sourceId] ?? "\(stablePrefix(root.path))::\(relation.sourceId)"
            copy.targetId = relation.targetId.hasPrefix("infrastructure:") ? relation.targetId : (projectIDMaps[root.standardizedFileURL.path]?[relation.targetId] ?? "\(stablePrefix(root.path))::\(relation.targetId)")
            return copy
        }
        var added: [ProjectNode] = [], removed: [ProjectNode] = [], changed: [ChangedProject] = []
        var addedRelations: [ArchitectureRelation] = [], removedRelations: [ArchitectureRelation] = []
        var flowChanges: [FlowChange] = [], distributedChanges: [DistributedFlowChange] = [], summary: [String] = []
        for entry in roots {
            guard let delta = entry.snapshot.delta else { continue }
            let root = entry.root.standardizedFileURL
            let prefix = stablePrefix(root.path)
            added += delta.addedProjects.map { mapProject($0, root: root) }
            removed += delta.removedProjects.map { mapProject($0, root: root) }
            changed += delta.changedProjects.map { item in
                var copy = item; copy.id = projectIDMaps[root.path]?[item.id] ?? "\(prefix)::\(item.id)"; return copy
            }
            addedRelations += delta.addedRelations.map { mapRelation($0, root: root) }
            removedRelations += delta.removedRelations.map { mapRelation($0, root: root) }
            flowChanges += (delta.flowChanges ?? []).map { item in
                var copy = item; copy.id = "\(prefix)::flow-change::\(item.id)"; copy.projectId = projectIDMaps[root.path]?[item.projectId] ?? "\(prefix)::\(item.projectId)"; return copy
            }
            distributedChanges += (delta.distributedFlowChanges ?? []).map { item in
                var copy = item; copy.id = "\(prefix)::distributed-change::\(item.id)"; return copy
            }
            summary += delta.summary.map { "\(label(for: root)): \($0)" }
        }
        let commitParts = roots.map { "\($0.snapshot.branch):\($0.snapshot.headCommit)" }.joined(separator: " | ")
        return ArchitectureDelta(previousCommit: deltas.map(\.previousCommit).joined(separator: " + "), currentCommit: commitParts,
            previousBranch: "workspace baseline", currentBranch: "workspace",
            addedProjects: added, removedProjects: removed, changedProjects: changed, addedRelations: addedRelations,
            removedRelations: removedRelations, summary: summary, flowChanges: flowChanges, distributedFlowChanges: distributedChanges)
    }
}
