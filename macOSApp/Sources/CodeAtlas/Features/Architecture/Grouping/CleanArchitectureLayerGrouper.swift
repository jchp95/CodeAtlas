import Foundation

/// Pure project-name based grouping for diagram presentation. This does not claim to infer
/// runtime layering; it groups same-root .NET projects whose names share a Clean Architecture base.
enum CleanArchitectureLayerGrouper {
    struct Group {
        let representative: ProjectNode
        let members: [ProjectNode]
        let displayName: String
    }

    private struct Candidate {
        let project: ProjectNode
        let base: String
        let key: String
        let layer: String
        let rank: Int
    }

    static func groups(from projects: [ProjectNode]) -> [Group] {
        let layerDefinitions: [(String, Int)] = [
            ("api", 0), ("presentation", 1), ("web", 1), ("application", 2), ("domain", 3),
            ("infrastructure", 4), ("persistence", 5), ("data", 5), ("contracts", 6),
            ("abstractions", 6), ("worker", 7), ("host", 8)
        ]
        var candidates: [Candidate] = []
        for project in projects where project.kind == "Dotnet" {
            let name = project.name.trimmingCharacters(in: .whitespacesAndNewlines)
            let lower = name.lowercased()
            for (layer, rank) in layerDefinitions {
                if let separator = [".", "-", "_"].first(where: { lower.hasSuffix($0 + layer) }) {
                    let base = String(name.dropLast(separator.count + layer.count))
                        .trimmingCharacters(in: CharacterSet(charactersIn: " .-_"))
                    guard base.count >= 3 else { continue }
                    let root = project.repositoryRoot?.lowercased() ?? ""
                    candidates.append(Candidate(project: project, base: base,
                        key: "\(root)|\(base.lowercased())", layer: layer, rank: rank))
                    break
                }
            }
        }

        var byKey: [String: [Candidate]] = [:]
        for candidate in candidates { byKey[candidate.key, default: []].append(candidate) }
        var consumedIDs = Set<String>()
        var output: [Group] = []
        for items in byKey.values where items.count >= 2 && Set(items.map(\.layer)).count >= 2 {
            let uniqueByID: [String: Candidate] = Dictionary(items.map { ($0.project.id, $0) }, uniquingKeysWith: { first, _ in first })
            let members: [Candidate] = uniqueByID.values.sorted { left, right in
                if left.rank != right.rank { return left.rank < right.rank }
                return left.project.name.localizedCaseInsensitiveCompare(right.project.name) == .orderedAscending
            }
            guard members.count >= 2 else { continue }
            let representative = members.first(where: { ["api", "presentation", "web"].contains($0.layer) }) ?? members[0]
            output.append(Group(representative: representative.project, members: members.map { $0.project }, displayName: representative.base))
            consumedIDs.formUnion(members.map { $0.project.id })
        }
        for project in projects where !consumedIDs.contains(project.id) {
            output.append(Group(representative: project, members: [project], displayName: project.name))
        }
        return output.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }
}
